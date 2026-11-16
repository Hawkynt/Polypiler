using System.Collections.Generic;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The characters a regular expression of the form <c>^[class]*$</c> or <c>^[class]+$</c> admits,
/// which a passed test proves every character of its subject to be one of (#1413). The class is a
/// list of characters and ranges <c>a-z</c>; a negated class, an escape, any other pattern and any
/// flag - <c>i</c> admits the other case, <c>m</c> makes the anchors lines - prove nothing.
/// </summary>
internal static class Alphabets {

  /// <summary>The characters the pattern admits, or <see langword="null"/> where it is of no such form.</summary>
  /// <param name="pattern">The pattern, as the literal writes it.</param>
  /// <param name="flags">The flags of the literal.</param>
  /// <returns>The characters.</returns>
  public static IReadOnlySet<char>? Of(string pattern, string flags) {
    if (flags.Length > 0 || pattern.Length < 5 || !pattern.StartsWith("^[", System.StringComparison.Ordinal) || pattern[^1] != '$' || pattern[^2] is not ('*' or '+') || pattern[^3] != ']')
      return null;

    var items = pattern[2..^3];
    if (items.Length == 0 || items[0] == '^' || items.IndexOfAny(['\\', '[', ']']) >= 0)
      return null;

    var alphabet = new HashSet<char>();
    for (var at = 0; at < items.Length; ++at) {
      // A '-' between two characters is a range; at either end it is the character itself.
      if (at + 2 < items.Length && items[at + 1] == '-') {
        for (int character = items[at]; character <= items[at + 2]; ++character)
          alphabet.Add((char)character);

        at += 2;
      } else
        alphabet.Add(items[at]);
    }

    return alphabet;
  }

}
