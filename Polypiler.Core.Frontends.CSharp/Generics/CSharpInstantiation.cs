using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// An instantiation of a public generic method that the module itself does not use, as
/// <c>--instantiate</c> names it: <c>IndexOf[double]</c>, <c>Pair[int,Box]</c>. The type arguments
/// are C# types - a keyword, a type of the file, an array of either.
/// </summary>
/// <param name="Method">The name of the generic method.</param>
/// <param name="TypeArguments">The type arguments, as C# writes them.</param>
public sealed record CSharpInstantiation(string Method, IReadOnlyList<string> TypeArguments) {

  /// <summary>Reads an instantiation as <c>--instantiate</c> writes it.</summary>
  /// <param name="text">The text: a name, then the type arguments in brackets, separated by commas.</param>
  /// <param name="instantiation">The instantiation, when the text is one.</param>
  /// <returns><see langword="true"/> when the text has that form.</returns>
  public static bool TryParse(string text, out CSharpInstantiation? instantiation) {
    ArgumentNullException.ThrowIfNull(text);
    instantiation = null;
    var open = text.IndexOf('[', StringComparison.Ordinal);
    if (open <= 0 || !text.EndsWith(']'))
      return false;

    var name = text[..open].Trim();
    var arguments = text[(open + 1)..^1].Split(',').Select(argument => argument.Trim()).ToList();
    if (!IsName(name) || arguments.Any(argument => argument.Length == 0 || !argument.All(character => char.IsLetterOrDigit(character) || character is '_' or '.' or '[' or ']')))
      return false;

    instantiation = new(name, arguments);
    return true;
  }

  private static bool IsName(string name) => (char.IsLetter(name[0]) || name[0] == '_') && name.All(character => char.IsLetterOrDigit(character) || character == '_');

}
