using System.Collections.Generic;
using System.Text.RegularExpressions;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// Reads the types a JSDoc comment states: <c>@param {type} name</c> and <c>@returns {type}</c>. The
/// spellings are the ones Cipher's <c>OpCodes</c> and <c>AlgorithmFramework</c> use: the integer and
/// floating-point widths, <c>byte</c>, <c>int</c>, <c>bool</c>, <c>string</c>, the names of the typed
/// arrays and <c>number</c>, an array of any of them (<c>uint8[][]</c> as well), the name of a class of
/// the file and <c>T|null</c>; a spelling that is not listed is no type.
/// </summary>
internal static class JsDoc {

  private static readonly Regex _parameter = new(@"@param\s*\{(?<type>(?:[^{}]|\{[^{}]*\})*)\}\s*(?<name>[^\s*]+)", RegexOptions.CultureInvariant);
  private static readonly Regex _result = new(@"@returns?\s*\{(?<type>[^{}]*)\}", RegexOptions.CultureInvariant);
  private static readonly Regex _type = new(@"@type\s*\{(?<type>[^{}]*)\}", RegexOptions.CultureInvariant);
  private static readonly Regex _typedef = new(@"@typedef\s*\{(?<form>[^{}]*)\}\s*(?<name>[A-Za-z_$][\w$]*)", RegexOptions.CultureInvariant);
  private static readonly Regex _property = new(@"@(?:property|prop)\s*\{(?<type>[^{}]*)\}\s*(?:\[\s*(?<optional>[A-Za-z_$][\w$]*)\s*(?:=[^\]]*)?\]|(?<name>[A-Za-z_$][\w$]*))", RegexOptions.CultureInvariant);

  /// <summary>
  /// The <c>@typedef</c> a comment declares, with the <c>@property</c> tags that follow it: its name, whether
  /// it is of the form <c>@typedef {Object}</c> - the only one the subset takes (#1409) - and every
  /// property in source order, as its key, the spelling of its type and whether it is optional,
  /// <c>[key]</c> or <c>[key=default]</c>. A comment without a typedef declares none.
  /// </summary>
  public static JsDocTypedef? Typedef(string comment) {
    var tag = _typedef.Match(comment);
    if (!tag.Success)
      return null;

    var properties = new List<JsDocProperty>();
    foreach (Match property in _property.Matches(comment, tag.Index + tag.Length)) {
      var optional = property.Groups["optional"].Success;
      properties.Add(new(optional ? property.Groups["optional"].Value : property.Groups["name"].Value, property.Groups["type"].Value.Trim(), optional));
    }

    return new(tag.Groups["name"].Value, tag.Groups["form"].Value.Trim() is "Object" or "object", properties);
  }

  /// <summary>The spelling of the type a <c>@type</c> tag of the comment states, or <see langword="null"/>.</summary>
  public static string? TypeSpelling(string? comment) => _type.Match(comment ?? "") is { Success: true } tag ? tag.Groups["type"].Value : null;

  /// <summary>The type a spelling states, as a parameter's: <see langword="null"/> for one that is no type.</summary>
  public static JsType? Stated(string spelling, System.Func<string, ClassModel?> classes) => Declared(spelling, classes);

  /// <summary>
  /// The type the comment states for a parameter: whether it states one at all, and the type, which
  /// is <see langword="null"/> when the spelling is not a supported one.
  /// </summary>
  /// <param name="comment">The comment, or <see langword="null"/>.</param>
  /// <param name="name">The parameter.</param>
  /// <param name="classes">The class of the file a name names, or <see langword="null"/> when none does.</param>
  public static (bool Stated, JsType? Type) ParameterType(string? comment, string name, System.Func<string, ClassModel?> classes) {
    foreach (Match tag in _parameter.Matches(comment ?? ""))
      if (tag.Groups["name"].Value == name)
        return (true, Declared(tag.Groups["type"].Value, classes));

    return (false, null);
  }

  /// <summary>The type the comment states for the result as the values it admits, or <see langword="null"/> when it states none or no type (#1413).</summary>
  public static JsType? StatedResult(string? comment, System.Func<string, ClassModel?> classes)
    => _result.Match(comment ?? "") is { Success: true } tag ? Declared(tag.Groups["type"].Value, classes) : null;

  /// <summary>The type the comment states for the result, or <see langword="null"/> when it states none or one that is no type of the IR on its own.</summary>
  public static IrType? ResultType(string? comment) {
    var tag = _result.Match(comment ?? "");
    return tag.Success ? TypeOf(tag.Groups["type"].Value) : null;
  }

  /// <summary>
  /// A spelling as the values it admits: a type of the IR, a class of the file (#1365), an array of
  /// either - a JavaScript <c>Array</c> where it holds what no typed array does - or one of them that
  /// may be <c>null</c>.
  /// </summary>
  private static JsType? Declared(string spelling, System.Func<string, ClassModel?> classes) {
    spelling = spelling.Trim();
    if (spelling.EndsWith("|null", System.StringComparison.Ordinal) && Declared(spelling[..^5], classes) is { } present)
      return JsType.Join(present, JsType.Null);

    // T[] of numbers is an Array or a typed array, which the calls decide (#1374).
    if (spelling.EndsWith("[]", System.StringComparison.Ordinal) && !spelling[..^2].EndsWith("[]", System.StringComparison.Ordinal) && TypeOf(spelling) is { } array)
      return array.Element!.IsNumeric ? JsType.Of(array).AsStated().AdmittingWhole() : JsType.Of(array);

    if (spelling.EndsWith("[]", System.StringComparison.Ordinal))
      return Declared(spelling[..^2], classes) is { } element && element.Kind is JsKind.Object or JsKind.Array ? JsType.ArrayOf(element, true) : null;

    // int64 and uint64 state a range, which Numbers or BigInts fill as the values decide (#1401).
    return TypeOf(spelling) is { } scalar ? (scalar.IsLong ? JsType.Of(scalar).AsStated() : JsType.Of(scalar)) : classes(spelling) is { } owner ? JsType.Object(owner) : null;
  }

  private static IrType? TypeOf(string spelling) {
    spelling = spelling.Trim();
    if (spelling.EndsWith("[]", System.StringComparison.Ordinal))
      return Scalar(spelling[..^2]) is { } element && (element.IsNumeric || element.IsString) ? IrType.Array(element) : null;

    return TypeInference.TypedArrays.TryGetValue(spelling, out var typed) ? IrType.Array(typed) : Scalar(spelling);
  }

  private static IrType? Scalar(string spelling) => spelling switch {
    "int8" => IrType.I8,
    "uint8" or "byte" => IrType.U8,
    "int16" => IrType.I16,
    "uint16" => IrType.U16,
    "int32" or "int" => IrType.I32,
    "uint32" => IrType.U32,
    "int64" => IrType.I64,
    "uint64" => IrType.U64,
    "float32" => IrType.F32,
    "number" or "float64" => IrType.F64,
    "boolean" or "bool" => IrType.Bool,
    "string" => IrType.String,
    _ => null
  };

}

/// <summary>A property of a <c>@typedef</c>: its key, the spelling of its type and whether a value may leave it out.</summary>
/// <param name="Key">The key.</param>
/// <param name="Spelling">The type as JSDoc spells it.</param>
/// <param name="IsOptional">Whether the key is optional, <c>[key]</c> or <c>[key=default]</c>: a value that leaves it out holds <c>undefined</c> there.</param>
internal sealed record JsDocProperty(string Key, string Spelling, bool IsOptional);

/// <summary>A <c>@typedef</c> of a comment.</summary>
/// <param name="Name">The name it declares.</param>
/// <param name="IsObject">Whether it is of the form <c>@typedef {Object}</c>, the only one the subset takes.</param>
/// <param name="Properties">Its properties, in source order.</param>
internal sealed record JsDocTypedef(string Name, bool IsObject, IReadOnlyList<JsDocProperty> Properties);
