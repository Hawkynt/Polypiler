using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Backends.Rust.Naming;

/// <summary>
/// IR names as Rust names (the Rust API Guidelines, C-CASE; The Rust Reference, "Keywords" and
/// "Identifiers"). A name is split into words at <c>_</c> and every character that is no letter or
/// digit, at a lower-case letter or a digit followed by an upper-case one, and before the last capital
/// of a run of capitals followed by a lower-case letter; the words are then joined in
/// <c>snake_case</c>, <c>UpperCamelCase</c> or <c>SCREAMING_SNAKE_CASE</c>. A result that is a keyword
/// of the edition becomes a raw identifier (<c>r#type</c>), except <c>self</c>, <c>Self</c>,
/// <c>super</c> and <c>crate</c>, which cannot be raw and take a trailing underscore. A leading digit
/// takes a leading underscore, and a name without a letter or a digit is <c>_v</c>.
/// </summary>
public static class RustNames {

  /// <summary>The strict and reserved keywords of every edition.</summary>
  private static readonly HashSet<string> _keywords = [
    "as", "break", "const", "continue", "crate", "else", "enum", "extern", "false", "fn", "for", "if", "impl", "in", "let", "loop",
    "match", "mod", "move", "mut", "pub", "ref", "return", "self", "Self", "static", "struct", "super", "trait", "true", "type",
    "unsafe", "use", "where", "while", "abstract", "become", "box", "do", "final", "macro", "override", "priv", "typeof", "unsized",
    "virtual", "yield"
  ];

  /// <summary>The keywords an edition adds: <c>async</c>, <c>await</c>, <c>dyn</c> and <c>try</c> in 2018, <c>gen</c> in 2024.</summary>
  private static readonly (int Edition, string Keyword)[] _editionKeywords = [
    (2018, "async"), (2018, "await"), (2018, "dyn"), (2018, "try"), (2024, "gen")
  ];

  /// <summary>The keywords that cannot be raw identifiers.</summary>
  private static readonly HashSet<string> _notRaw = ["self", "Self", "super", "crate"];

  /// <summary>Whether a word is a keyword of an edition.</summary>
  /// <param name="word">The word.</param>
  /// <param name="edition">The edition, such as 2024.</param>
  public static bool IsKeyword(string word, int edition)
    => _keywords.Contains(word) || _editionKeywords.Any(entry => entry.Edition <= edition && entry.Keyword == word);

  /// <summary>The words of a name, as the guidelines split it.</summary>
  /// <param name="name">The name.</param>
  /// <returns>The words, in their original case.</returns>
  public static IReadOnlyList<string> Words(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var words = new List<string>();
    var current = new StringBuilder();
    void Flush() {
      if (current.Length > 0)
        words.Add(current.ToString());
      current.Clear();
    }

    for (var index = 0; index < name.Length; ++index) {
      var c = name[index];
      if (!char.IsAsciiLetterOrDigit(c)) {
        Flush();
        continue;
      }

      if (current.Length > 0 && char.IsAsciiLetterUpper(c)) {
        var previous = name[index - 1];
        var next = index + 1 < name.Length ? name[index + 1] : '\0';
        if (char.IsAsciiLetterLower(previous) || char.IsAsciiDigit(previous) || (char.IsAsciiLetterUpper(previous) && char.IsAsciiLetterLower(next)))
          Flush();
      }

      current.Append(c);
    }

    Flush();
    return words;
  }

  /// <summary>A name in <c>snake_case</c>: functions, modules, parameters, locals, the package.</summary>
  /// <param name="name">The IR name.</param>
  /// <param name="edition">The edition, whose keywords are escaped.</param>
  public static string Snake(string name, int edition) => Escaped(string.Join('_', Words(name).Select(word => word.ToLowerInvariant())), edition);

  /// <summary>A name in <c>SCREAMING_SNAKE_CASE</c>: constants and statics.</summary>
  /// <param name="name">The IR name.</param>
  /// <param name="edition">The edition.</param>
  public static string Screaming(string name, int edition) => Escaped(string.Join('_', Words(name).Select(word => word.ToUpperInvariant())), edition);

  /// <summary>A name in <c>UpperCamelCase</c>: types.</summary>
  /// <param name="name">The IR name.</param>
  /// <param name="edition">The edition.</param>
  public static string UpperCamel(string name, int edition)
    => Escaped(string.Concat(Words(name).Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant())), edition);

  /// <summary>A joined name made a valid identifier of the edition.</summary>
  private static string Escaped(string name, int edition) {
    if (name.Length == 0)
      return "_v";

    if (char.IsAsciiDigit(name[0]))
      return "_" + name;

    if (_notRaw.Contains(name))
      return name + "_";

    return IsKeyword(name, edition) ? "r#" + name : name;
  }

  /// <summary>
  /// Gives names to IR names in declaration order, each converted by <paramref name="convert"/>: a name
  /// that collides with one given before takes the suffix <c>_2</c>, <c>_3</c> and so on.
  /// </summary>
  /// <param name="names">The IR names, in declaration order.</param>
  /// <param name="convert">The conversion.</param>
  /// <param name="taken">Names that are taken already; the ones given are added.</param>
  /// <returns>The Rust name of each IR name, in order.</returns>
  public static IReadOnlyList<string> Unique(IEnumerable<string> names, Func<string, string> convert, ISet<string> taken) {
    ArgumentNullException.ThrowIfNull(names);
    ArgumentNullException.ThrowIfNull(convert);
    ArgumentNullException.ThrowIfNull(taken);
    var result = new List<string>();
    foreach (var name in names) {
      var converted = convert(name);
      var candidate = converted;
      for (var suffix = 2; !taken.Add(candidate); ++suffix)
        candidate = (converted.StartsWith("r#", StringComparison.Ordinal) ? converted[2..] : converted) + "_" + suffix;
      result.Add(candidate);
    }

    return result;
  }

}
