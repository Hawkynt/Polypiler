using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Backends.Python;

/// <summary>
/// The names a module gets in Python (PEP 8): functions, parameters and locals in snake_case,
/// constants in UPPER_SNAKE_CASE, everything that is not public behind a leading underscore. A name
/// that would be a keyword, a soft keyword or a built-in the generated code uses gets a trailing
/// underscore; one that would start with a digit gets the prefix <c>n_</c>.
/// </summary>
public static class PythonNaming {

  /// <summary>The keywords of Python 3 (The Python Language Reference, §2.3.1), which no name may be.</summary>
  public static IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.Ordinal) {
    "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else",
    "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise",
    "return", "try", "while", "with", "yield"
  };

  /// <summary>
  /// The names no generated name takes besides the keywords: the soft keywords <c>match</c>,
  /// <c>case</c>, <c>type</c> and <c>_</c>, the built-ins, and the modules and names the generated
  /// code imports.
  /// </summary>
  public static IReadOnlySet<string> Reserved { get; } = new HashSet<string>(StringComparer.Ordinal) {
    "match", "case", "type", "_",
    "abs", "all", "any", "bin", "bool", "bytearray", "bytes", "callable", "chr", "divmod", "enumerate", "filter", "float", "format",
    "hash", "hex", "id", "input", "int", "isinstance", "iter", "len", "list", "map", "max", "min", "next", "object", "oct", "open",
    "ord", "pow", "print", "range", "repr", "reversed", "round", "set", "slice", "sorted", "str", "sum", "super", "tuple", "zip",
    "ArithmeticError", "IndexError", "OverflowError", "ZeroDivisionError", "AssertionError", "ValueError", "TypeError",
    "math", "struct", "typing", "List", "self", "cls"
  };

  /// <summary>
  /// A name in snake_case: an upper-case letter that follows a lower-case letter or a digit, or that
  /// starts a word after an acronym, begins a new word; every letter is lower case; a character that
  /// is no ASCII letter, digit or underscore becomes an underscore.
  /// </summary>
  /// <param name="name">The name as the IR has it.</param>
  /// <returns>The snake_case form; empty for an empty name.</returns>
  public static string Snake(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var text = new StringBuilder();
    for (var index = 0; index < name.Length; ++index) {
      var character = name[index];
      if (char.IsAsciiLetterUpper(character)) {
        var previous = index > 0 ? name[index - 1] : '\0';
        var next = index + 1 < name.Length ? name[index + 1] : '\0';
        if (index > 0 && (char.IsAsciiLetterLower(previous) || char.IsAsciiDigit(previous) || (char.IsAsciiLetterUpper(previous) && char.IsAsciiLetterLower(next))))
          text.Append('_');

        text.Append(char.ToLowerInvariant(character));
      } else {
        text.Append(char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_');
      }
    }

    return text.ToString();
  }

  /// <summary>A name made usable: <c>n_</c> in front of a leading digit, an underscore behind a keyword or a reserved name.</summary>
  /// <param name="name">The name.</param>
  /// <returns>The name Python accepts.</returns>
  public static string Usable(string name) {
    ArgumentNullException.ThrowIfNull(name);
    if (name.Length > 0 && char.IsAsciiDigit(name[0]))
      name = "n_" + name;

    return Keywords.Contains(name) || Reserved.Contains(name) ? name + "_" : name;
  }

  /// <summary>The Python name of a function: snake_case, with a leading underscore when it is not public.</summary>
  /// <param name="name">The name in the IR.</param>
  /// <param name="isPublic">Whether the function is visible outside the module.</param>
  /// <returns>The name, or empty when nothing of the name is left.</returns>
  public static string Function(string name, bool isPublic) {
    var snake = Snake(name).Trim('_');
    if (snake.Length == 0)
      return "";

    return isPublic ? Usable(snake) : "_" + snake;
  }

  /// <summary>The Python name of a constant of the module: UPPER_SNAKE_CASE behind a leading underscore.</summary>
  /// <param name="name">The name in the IR.</param>
  /// <returns>The name.</returns>
  public static string Constant(string name) => "_" + Usable(Snake(name).Trim('_').ToUpperInvariant() is { Length: > 0 } upper ? upper : "CONSTANT");

  /// <summary>The Python name of a global of the module: snake_case behind a leading underscore.</summary>
  /// <param name="name">The name in the IR.</param>
  /// <returns>The name.</returns>
  public static string Global(string name) => "_" + Usable(Snake(name).Trim('_') is { Length: > 0 } snake ? snake : "state");

  /// <summary>The Python name of a parameter or local: snake_case, made usable.</summary>
  /// <param name="name">The name in the IR.</param>
  /// <returns>The name.</returns>
  public static string Local(string name) => Usable(Snake(name) is { Length: > 0 } snake ? snake : "value");

  /// <summary>The name of the file a module is written to: its name in snake_case, which is also what <c>import</c> names it by.</summary>
  /// <param name="name">The name of the IR module.</param>
  /// <returns>The module name, without <c>.py</c>.</returns>
  public static string Module(string name) => Usable(Snake(name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name).Trim('_') is { Length: > 0 } snake ? snake : "module");

  /// <summary>
  /// A name that is not taken yet: the name itself, or the name with the smallest number from 2 on
  /// behind an underscore that is free.
  /// </summary>
  /// <param name="name">The name wanted.</param>
  /// <param name="taken">Whether a name is taken.</param>
  /// <returns>The free name.</returns>
  public static string Free(string name, Func<string, bool> taken) {
    ArgumentNullException.ThrowIfNull(taken);
    if (!taken(name))
      return name;

    for (var suffix = 2; ; ++suffix) {
      var candidate = name + "_" + suffix.ToString(CultureInfo.InvariantCulture);
      if (!taken(candidate))
        return candidate;
    }
  }

  /// <summary>Whether a text is an identifier of the ASCII subset the backend writes.</summary>
  /// <param name="name">The text.</param>
  /// <returns><see langword="true"/> for a letter or underscore followed by letters, digits and underscores, that is no keyword.</returns>
  public static bool IsIdentifier(string name)
    => name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') && !Keywords.Contains(name);

}
