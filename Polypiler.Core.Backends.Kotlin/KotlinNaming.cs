using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Backends.Kotlin;

/// <summary>
/// The names of the Kotlin a module is written as, after the Kotlin coding conventions: functions,
/// parameters and locals in lowerCamelCase, types in UpperCamelCase, constants in SCREAMING_SNAKE_CASE,
/// the package in lower case. A name that is a hard keyword of Kotlin, or one of the names of the
/// standard library the emitted code calls without a qualifier, takes a trailing underscore.
/// </summary>
public static class KotlinNaming {

  /// <summary>The hard keywords of Kotlin (kotlinlang.org/docs/keyword-reference.html), which no name may be.</summary>
  public static IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.Ordinal) {
    "as", "break", "class", "continue", "do", "else", "false", "for", "fun", "if", "in", "interface", "is", "null",
    "object", "package", "return", "super", "this", "throw", "true", "try", "typealias", "typeof", "val", "var", "when", "while"
  };

  /// <summary>
  /// The names the emitted code uses without a qualifier: a declaration of the file with one of them
  /// would take its place, since the file's own declarations come before the default imports.
  /// </summary>
  public static IReadOnlySet<string> Reserved { get; } = new HashSet<string>(StringComparer.Ordinal) {
    "println", "minOf", "maxOf", "arrayOf", "arrayOfNulls", "booleanArrayOf", "byteArrayOf", "shortArrayOf", "intArrayOf", "longArrayOf",
    "floatArrayOf", "doubleArrayOf", "emptyArray", "require", "check", "error", "repeat", "run", "sqrt", "floor", "ceil", "truncate",
    "round", "abs", "sign", "withSign", "Math", "StrictMath", "Integer", "Character", "Char", "Array", "String", "Boolean", "Byte",
    "Short", "Int", "Long", "Float", "Double", "UByte", "UShort", "UInt", "ULong", "Unit", "Any", "Nothing", "Exception",
    "ArithmeticException", "IllegalStateException", "IllegalArgumentException", "IndexOutOfBoundsException",
    "ArrayIndexOutOfBoundsException", "NullPointerException", "ClassCastException", "NoSuchElementException",
    "NumberFormatException", "AssertionError", "Throwable", "java", "kotlin", "it", "field", "value"
  };

  /// <summary>A name with every character that cannot be part of a Kotlin identifier replaced by <c>_</c>; empty when nothing is left.</summary>
  private static string Clean(string name) {
    var text = new StringBuilder(name.Length);
    foreach (var character in name)
      text.Append(char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_');

    var cleaned = text.ToString().Trim('_');
    return cleaned.Length > 0 && char.IsAsciiDigit(cleaned[0]) ? "_" + cleaned : cleaned;
  }

  /// <summary>A name that is no keyword and no reserved name: one of them takes a trailing underscore.</summary>
  private static string Safe(string name) => Keywords.Contains(name) || Reserved.Contains(name) ? name + "_" : name;

  /// <summary>
  /// The lowerCamelCase of a name: the leading run of capitals in lower case, except its last
  /// capital where a lower-case letter follows it (<c>HTTPServer</c> to <c>httpServer</c>,
  /// <c>Crc32</c> to <c>crc32</c>); <c>_</c> separators are dropped and the letter after one is
  /// capitalised (<c>max_value</c> to <c>maxValue</c>).
  /// </summary>
  /// <param name="name">The name.</param>
  /// <returns>The name, never a keyword or a reserved name; empty when nothing of it is left.</returns>
  public static string Camel(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var words = Clean(name).Split('_', StringSplitOptions.RemoveEmptyEntries);
    if (words.Length == 0)
      return "";

    var text = new StringBuilder(Lower(words[0]));
    foreach (var word in words.Skip(1))
      text.Append(char.ToUpperInvariant(word[0])).Append(word.AsSpan(1));

    var result = text.ToString();
    return Safe(char.IsAsciiDigit(result[0]) ? "_" + result : result);
  }

  private static string Lower(string word) {
    var run = 0;
    while (run < word.Length && char.IsAsciiLetterUpper(word[run]))
      ++run;

    if (run == 0)
      return word;

    // The last capital of a run that a lower-case letter follows begins the next word.
    if (run > 1 && run < word.Length && char.IsAsciiLetterLower(word[run]))
      --run;

    return word[..run].ToLowerInvariant() + word[run..];
  }

  /// <summary>The UpperCamelCase of a name, for a type: its first letter in upper case, <c>_</c> separators dropped.</summary>
  /// <param name="name">The name.</param>
  /// <returns>The name; empty when nothing of it is left.</returns>
  public static string Type(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var words = Clean(name).Split('_', StringSplitOptions.RemoveEmptyEntries);
    var result = string.Concat(words.Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    return result.Length == 0 ? "" : Safe(char.IsAsciiDigit(result[0]) ? "T" + result : result);
  }

  /// <summary>
  /// The SCREAMING_SNAKE_CASE of a name, for a constant: an underscore between a lower-case letter or
  /// digit and the capital after it, everything in upper case (<c>MaxValue</c> to <c>MAX_VALUE</c>,
  /// <c>_table</c> to <c>TABLE</c>).
  /// </summary>
  /// <param name="name">The name.</param>
  /// <returns>The name; empty when nothing of it is left.</returns>
  public static string Constant(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var clean = Clean(name);
    var text = new StringBuilder(clean.Length + 4);
    for (var index = 0; index < clean.Length; ++index) {
      var character = clean[index];
      if (index > 0 && char.IsAsciiLetterUpper(character) && (char.IsAsciiLetterLower(clean[index - 1]) || char.IsAsciiDigit(clean[index - 1])))
        text.Append('_');

      text.Append(char.ToUpperInvariant(character));
    }

    var result = text.ToString();
    return result.Length == 0 ? "" : Safe(result);
  }

  /// <summary>The package of a module: its name in lower case, every character that is no letter or digit dropped, a keyword with an underscore.</summary>
  /// <param name="module">The name of the module.</param>
  /// <returns>The package; <c>generated</c> when nothing of the name is left.</returns>
  public static string Package(string module) {
    ArgumentNullException.ThrowIfNull(module);
    var letters = new string([.. module.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant)]);
    if (letters.Length == 0 || char.IsAsciiDigit(letters[0]))
      letters = "generated" + letters;

    return Keywords.Contains(letters) ? letters + "_" : letters;
  }

  /// <summary>The first of <c>name</c>, <c>name2</c>, <c>name3</c>, … that is not taken.</summary>
  /// <param name="name">The wanted name.</param>
  /// <param name="taken">Whether a name is taken.</param>
  /// <returns>The free name.</returns>
  public static string Free(string name, Func<string, bool> taken) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(taken);
    if (!taken(name))
      return name;

    for (var suffix = 2; ; ++suffix) {
      var candidate = name.EndsWith('_') ? name + suffix : $"{name}{suffix}";
      if (!taken(candidate))
        return candidate;
    }
  }

}
