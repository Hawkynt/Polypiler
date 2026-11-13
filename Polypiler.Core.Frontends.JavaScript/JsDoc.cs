using System.Text.RegularExpressions;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// Reads the types a JSDoc comment states: <c>@param {type} name</c> and <c>@returns {type}</c>. The
/// spellings are the ones Cipher's <c>OpCodes</c> uses; a spelling that is not listed is no type.
/// </summary>
internal static class JsDoc {

  private static readonly Regex _parameter = new(@"@param\s*\{(?<type>[^{}]*)\}\s*(?<name>[^\s*]+)", RegexOptions.CultureInvariant);
  private static readonly Regex _result = new(@"@returns?\s*\{(?<type>[^{}]*)\}", RegexOptions.CultureInvariant);

  /// <summary>The type of a parameter, or <see langword="null"/> when the comment gives it no supported one.</summary>
  public static IrType? ParameterType(string? comment, string name) {
    if (comment is null)
      return null;

    foreach (Match tag in _parameter.Matches(comment))
      if (tag.Groups["name"].Value == name)
        return TypeOf(tag.Groups["type"].Value);

    return null;
  }

  /// <summary>
  /// The type of the result: nothing when there is no <c>@returns</c> or it says <c>void</c>, and
  /// <see langword="null"/> when it names a type that is not supported.
  /// </summary>
  public static IrType? ResultType(string? comment) {
    var tag = comment is null ? Match.Empty : _result.Match(comment);
    if (!tag.Success)
      return IrType.Void;

    var spelling = tag.Groups["type"].Value.Trim();
    return spelling == "void" ? IrType.Void : TypeOf(spelling);
  }

  private static IrType? TypeOf(string spelling) => spelling.Trim() switch {
    "int32" => IrType.I32,
    "uint32" => IrType.U32,
    "uint8" => IrType.U8,
    "boolean" => IrType.Bool,
    "uint8[]" or "Uint8Array" => IrType.Array(IrType.U8),
    "int32[]" or "Int32Array" => IrType.Array(IrType.I32),
    "uint32[]" or "Uint32Array" => IrType.Array(IrType.U32),
    _ => null
  };

}
