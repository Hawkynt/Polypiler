using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>The words no JavaScript identifier can be, at any level and in strict code.</summary>
internal static class JavaScriptWords {

  /// <summary>The reserved words of ECMAScript 5.1 to 2022, with those only strict code and modules reserve.</summary>
  public static readonly IReadOnlySet<string> Reserved = new HashSet<string> {
    "arguments", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default",
    "delete", "do", "else", "enum", "eval", "export", "extends", "false", "finally", "for", "function",
    "if", "implements", "import", "in", "instanceof", "interface", "let", "new", "null", "package",
    "private", "protected", "public", "return", "static", "super", "switch", "this", "throw", "true",
    "try", "typeof", "var", "void", "while", "with", "yield"
  };

  /// <summary>
  /// Whether a text is an identifier every level accepts and no level reserves: a letter of ASCII,
  /// <c>_</c> or <c>$</c>, followed by those and digits.
  /// </summary>
  public static bool IsIdentifier(string text)
    => text.Length > 0 && !char.IsAsciiDigit(text[0]) && text.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '_' or '$') && !Reserved.Contains(text);

}
