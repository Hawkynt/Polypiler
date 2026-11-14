using System.Text.RegularExpressions;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// Reads the types a JSDoc comment states: <c>@param {type} name</c> and <c>@returns {type}</c>. The
/// spellings are the ones Cipher's <c>OpCodes</c> uses, the names of the typed arrays and <c>number</c>; a
/// spelling that is not listed is no type.
/// </summary>
internal static class JsDoc {

  private static readonly Regex _parameter = new(@"@param\s*\{(?<type>(?:[^{}]|\{[^{}]*\})*)\}\s*(?<name>[^\s*]+)", RegexOptions.CultureInvariant);
  private static readonly Regex _result = new(@"@returns?\s*\{(?<type>[^{}]*)\}", RegexOptions.CultureInvariant);

  /// <summary>
  /// The type the comment states for a parameter: whether it states one at all, and the type, which
  /// is <see langword="null"/> when the spelling is not a supported one.
  /// </summary>
  public static (bool Stated, IrType? Type) ParameterType(string? comment, string name) {
    foreach (Match tag in _parameter.Matches(comment ?? ""))
      if (tag.Groups["name"].Value == name)
        return (true, TypeOf(tag.Groups["type"].Value));

    return (false, null);
  }

  /// <summary>The type the comment states for the result, or <see langword="null"/> when it states none or one that is not supported.</summary>
  public static IrType? ResultType(string? comment) {
    var tag = _result.Match(comment ?? "");
    return tag.Success ? TypeOf(tag.Groups["type"].Value) : null;
  }

  private static IrType? TypeOf(string spelling) {
    spelling = spelling.Trim();
    if (spelling.EndsWith("[]", System.StringComparison.Ordinal))
      return Scalar(spelling[..^2]) is { } element && (element.IsNumeric || element.IsString) ? IrType.Array(element) : null;

    return Inference.TypeInference.TypedArrays.TryGetValue(spelling, out var typed) ? IrType.Array(typed) : Scalar(spelling);
  }

  private static IrType? Scalar(string spelling) => spelling switch {
    "int8" => IrType.I8,
    "uint8" => IrType.U8,
    "int16" => IrType.I16,
    "uint16" => IrType.U16,
    "int32" => IrType.I32,
    "uint32" => IrType.U32,
    "int64" => IrType.I64,
    "uint64" => IrType.U64,
    "float32" => IrType.F32,
    "number" or "float64" => IrType.F64,
    "boolean" => IrType.Bool,
    "string" => IrType.String,
    _ => null
  };

}
