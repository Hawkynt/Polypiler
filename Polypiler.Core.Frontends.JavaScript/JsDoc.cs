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

    if (spelling.EndsWith("[]", System.StringComparison.Ordinal) && !spelling[..^2].EndsWith("[]", System.StringComparison.Ordinal) && TypeOf(spelling) is { } array)
      return JsType.Of(array);

    if (spelling.EndsWith("[]", System.StringComparison.Ordinal))
      return Declared(spelling[..^2], classes) is { } element && element.Kind is JsKind.Object or JsKind.Array ? JsType.ArrayOf(element, true) : null;

    return TypeOf(spelling) is { } scalar ? JsType.Of(scalar) : classes(spelling) is { } owner ? JsType.Object(owner) : null;
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
