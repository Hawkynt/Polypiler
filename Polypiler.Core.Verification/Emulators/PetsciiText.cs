using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Verification.Emulators;

/// <summary>
/// The text form of PETSCII in the upper-case/graphics character set, which the screen output of a
/// run and a normalised listing share: <c>$20</c>-<c>$5F</c> as the ASCII character of the same code
/// (<c>\</c> for the pound sign, <c>^</c> and <c>_</c> for the arrows), <c>$0D</c> as a line end, and
/// every other byte as <c>{$XX}</c> with two upper-case hexadecimal digits.
/// </summary>
public static class PetsciiText {

  /// <summary>The text of PETSCII bytes.</summary>
  /// <param name="bytes">The bytes, as the machine printed them.</param>
  /// <returns>The text, one <c>\n</c> per <c>$0D</c>.</returns>
  public static string Decode(ReadOnlySpan<byte> bytes) {
    var text = new StringBuilder(bytes.Length);
    foreach (var value in bytes)
      if (value == 0x0D)
        text.Append('\n');
      else if (value is >= 0x20 and <= 0x5F)
        text.Append((char)value);
      else
        text.Append(Escape(value));

    return text.ToString();
  }

  /// <summary>The escape of a byte: <c>{$XX}</c>.</summary>
  /// <param name="value">The byte.</param>
  public static string Escape(byte value) => "{$" + value.ToString("X2", CultureInfo.InvariantCulture) + "}";

  /// <summary>Text as a screen comparison sees it: each line's trailing blanks and the empty lines at the end removed.</summary>
  /// <param name="text">The text, lines separated by <c>\n</c>.</param>
  public static string Trim(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var lines = text.Split('\n').Select(line => line.TrimEnd(' ')).ToList();
    while (lines.Count > 0 && lines[^1].Length == 0)
      lines.RemoveAt(lines.Count - 1);

    return string.Join('\n', lines);
  }

  /// <summary>Splits text into its characters and its <c>{…}</c> escapes, each escape one element.</summary>
  /// <param name="text">The text.</param>
  /// <returns>The elements in order; an escape element starts with <c>{</c>, an unclosed <c>{</c> is a character.</returns>
  internal static IEnumerable<string> Elements(string text) {
    for (var index = 0; index < text.Length; ++index) {
      var end = text[index] == '{' ? text.IndexOf('}', index) : -1;
      if (end < 0) {
        yield return text[index].ToString();
        continue;
      }

      yield return text[index..(end + 1)];
      index = end;
    }
  }

}
