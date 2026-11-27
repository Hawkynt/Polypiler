using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Backends.Go;

/// <summary>
/// The names a package gets in Go ("Effective Go", "Names"): an exported name in MixedCaps with an
/// upper-case first letter, every other in mixedCaps with a lower-case one, the initialisms of the Go
/// code review conventions in one case (<c>GetID</c>, <c>crc32Update</c>, <c>CRC32Update</c>), no
/// underscores. A name is split into words at underscores and at changes of case; a name that would
/// be a keyword or a predeclared identifier gets a trailing underscore, one that would start with a
/// digit the prefix <c>X</c> (exported) or <c>_</c> (unexported).
/// </summary>
public static class GoNaming {

  /// <summary>The keywords of Go (The Go Programming Language Specification, "Keywords"), which no name may be.</summary>
  public static IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.Ordinal) {
    "break", "case", "chan", "const", "continue", "default", "defer", "else", "fallthrough", "for", "func", "go", "goto", "if",
    "import", "interface", "map", "package", "range", "return", "select", "struct", "switch", "type", "var"
  };

  /// <summary>
  /// The names no generated name takes besides the keywords: the predeclared identifiers
  /// ("Predeclared identifiers") and the packages the generated code imports.
  /// </summary>
  public static IReadOnlySet<string> Reserved { get; } = new HashSet<string>(StringComparer.Ordinal) {
    "any", "bool", "byte", "comparable", "complex64", "complex128", "error", "float32", "float64", "int", "int8", "int16", "int32",
    "int64", "rune", "string", "uint", "uint8", "uint16", "uint32", "uint64", "uintptr", "true", "false", "iota", "nil",
    "append", "cap", "clear", "close", "complex", "copy", "delete", "imag", "len", "make", "max", "min", "new", "panic", "print",
    "println", "real", "recover", "math", "bits", "fmt", "strconv", "strings", "slices", "testing", "errors", "utf16", "unicode", "_"
  };

  /// <summary>The initialisms written in one case (Go code review comments, "Initialisms").</summary>
  public static IReadOnlySet<string> Initialisms { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    "ID", "CRC", "URL", "HTTP", "UTF", "ASCII", "API", "RNG", "IO", "FNV"
  };

  /// <summary>
  /// The words of a name: split at underscores and at every character that is no ASCII letter or
  /// digit, before an upper-case letter that follows a lower-case letter or a digit, and before the
  /// last upper-case letter of a run that a lower-case letter follows (<c>HTTPServer</c> is
  /// <c>HTTP</c>, <c>Server</c>).
  /// </summary>
  /// <param name="name">The name as the IR has it.</param>
  /// <returns>The words, none empty.</returns>
  public static IReadOnlyList<string> Words(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var words = new List<string>();
    var word = new StringBuilder();
    void Flush() {
      if (word.Length > 0)
        words.Add(word.ToString());

      word.Clear();
    }

    for (var index = 0; index < name.Length; ++index) {
      var character = name[index];
      if (!char.IsAsciiLetterOrDigit(character)) {
        Flush();
        continue;
      }

      if (char.IsAsciiLetterUpper(character) && word.Length > 0) {
        var previous = name[index - 1];
        var next = index + 1 < name.Length ? name[index + 1] : '\0';
        if (char.IsAsciiLetterLower(previous) || char.IsAsciiDigit(previous) || (char.IsAsciiLetterUpper(previous) && char.IsAsciiLetterLower(next)))
          Flush();
      }

      word.Append(character);
    }

    Flush();
    return words;
  }

  /// <summary>A name in MixedCaps: exported with an upper-case first letter, otherwise with a lower-case one.</summary>
  /// <param name="name">The name as the IR has it.</param>
  /// <param name="exported">Whether the name is exported.</param>
  /// <returns>The name, usable in Go; <c>X</c> or <c>value</c> when nothing of the name is left.</returns>
  public static string Mixed(string name, bool exported) {
    var words = Words(name);
    if (words.Count == 0)
      return exported ? "X" : "value";

    var text = new StringBuilder();
    for (var index = 0; index < words.Count; ++index) {
      var word = words[index];
      // An initialism followed by digits only, crc32 or utf8, is an initialism too.
      if (Initialisms.Contains(word.TrimEnd(Digits)) && word.TrimEnd(Digits).Length > 0)
        text.Append(index == 0 && !exported ? word.ToLowerInvariant() : word.ToUpperInvariant());
      else if (index == 0 && !exported)
        text.Append(char.ToLowerInvariant(word[0])).Append(word[1..]);
      else
        text.Append(char.ToUpperInvariant(word[0])).Append(word[1..]);
    }

    var result = text.ToString();
    if (char.IsAsciiDigit(result[0]))
      result = (exported ? "X" : "_") + result;

    return Usable(result);
  }

  /// <summary>A name made usable: an underscore behind a keyword or a reserved name.</summary>
  /// <param name="name">The name.</param>
  /// <returns>The name Go accepts.</returns>
  public static string Usable(string name) {
    ArgumentNullException.ThrowIfNull(name);
    return Keywords.Contains(name) || Reserved.Contains(name) ? name + "_" : name;
  }

  /// <summary>The Go name of a function: exported when it is public.</summary>
  /// <param name="name">The name in the IR.</param>
  /// <param name="isPublic">Whether the function is visible outside the module.</param>
  /// <returns>The name.</returns>
  public static string Function(string name, bool isPublic) {
    ArgumentNullException.ThrowIfNull(name);
    // An instantiation of a generic definition names its type arguments: Def<bool> is DefOfBool.
    return Mixed(name.Replace("<", "_of_", StringComparison.Ordinal).Replace(",", "_and_", StringComparison.Ordinal), isPublic);
  }

  /// <summary>The Go name of a parameter or local: mixedCaps, unexported.</summary>
  /// <param name="name">The name in the IR.</param>
  /// <returns>The name.</returns>
  public static string Local(string name) => Mixed(name, false);

  /// <summary>
  /// The name of the package a module is written to: the last element of the module path, or the
  /// module's name, in lower case without underscores; the element before a major-version suffix
  /// such as <c>/v2</c>.
  /// </summary>
  /// <param name="path">The module path or the name of the IR module.</param>
  /// <returns>The package name.</returns>
  public static string Package(string path) {
    ArgumentNullException.ThrowIfNull(path);
    var elements = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (elements.Length > 1 && elements[^1].Length > 1 && elements[^1][0] == 'v' && elements[^1][1..].All(char.IsAsciiDigit))
      elements = elements[..^1];

    var last = elements.Length == 0 ? "" : elements[^1];
    if (last.Contains('.') && elements.Length == 1)
      last = last[(last.LastIndexOf('.') + 1)..];

    var name = string.Concat(last.Where(char.IsAsciiLetterOrDigit)).ToLowerInvariant();
    if (name.Length == 0 || char.IsAsciiDigit(name[0]))
      name = "p" + name;

    return Usable(name);
  }

  /// <summary>The name of the file of a module: the module's name in snake_case, <c>.go</c>.</summary>
  /// <param name="name">The name of the IR module.</param>
  /// <returns>The file name.</returns>
  public static string File(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var last = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
    var words = Words(last).Select(word => word.ToLowerInvariant()).ToList();
    var stem = words.Count == 0 ? "module" : string.Join("_", words);
    // A file whose name ends in _test, or in _<GOOS> or _<GOARCH>, would be built only under that condition.
    if (stem.EndsWith("_test", StringComparison.Ordinal) || _buildSuffixes.Any(suffix => stem.EndsWith("_" + suffix, StringComparison.Ordinal)))
      stem += "_go";

    return stem + ".go";
  }

  private static readonly char[] Digits = ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9'];

  private static readonly string[] _buildSuffixes = [
    "aix", "android", "darwin", "dragonfly", "freebsd", "illumos", "ios", "js", "linux", "netbsd", "openbsd", "plan9", "solaris",
    "wasip1", "windows", "386", "amd64", "arm", "arm64", "loong64", "mips", "mipsle", "mips64", "mips64le", "ppc64", "ppc64le",
    "riscv64", "s390x", "wasm"
  ];

  /// <summary>
  /// A name that is not taken yet: the name itself, or the name with the smallest number from 2 on
  /// that is free.
  /// </summary>
  /// <param name="name">The name wanted.</param>
  /// <param name="taken">Whether a name is taken.</param>
  /// <returns>The free name.</returns>
  public static string Free(string name, Func<string, bool> taken) {
    ArgumentNullException.ThrowIfNull(taken);
    if (!taken(name))
      return name;

    var stem = name.EndsWith('_') ? name[..^1] : name;
    for (var suffix = 2; ; ++suffix) {
      var candidate = stem + suffix.ToString(CultureInfo.InvariantCulture);
      if (!taken(candidate))
        return candidate;
    }
  }

  /// <summary>Whether a text is a valid Go module path of the subset: elements of lower-case letters, digits, <c>-</c>, <c>.</c>, <c>_</c> and <c>~</c>, separated by <c>/</c>, the first with a dot or a single element.</summary>
  /// <param name="path">The path.</param>
  /// <returns>Whether the <c>go</c> command accepts it.</returns>
  public static bool IsModulePath(string? path) {
    if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.EndsWith('/'))
      return false;

    var elements = path.Split('/');
    return elements.All(element => element.Length > 0 && element != "." && element != ".." && !element.StartsWith('.') && !element.EndsWith('.')
      && element.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '.' or '_' or '~'));
  }

}
