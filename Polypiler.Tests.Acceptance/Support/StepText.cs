using System;
using System.Globalization;
using System.Text;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary>Text quoted in a step.</summary>
public static class StepText {

  /// <summary>The regular expression of a double-quoted text in which <c>\"</c> stands for a quote.</summary>
  public const string Quoted = "\"((?:[^\"\\\\]|\\\\.)*)\"";

  /// <summary>
  /// The text a quoted step argument stands for: <c>\n</c> is a line feed, <c>\t</c> a tab, <c>\\</c>
  /// a backslash and <c>\"</c> a quote.
  /// </summary>
  /// <exception cref="FormatException">A backslash is followed by anything else or by nothing.</exception>
  public static string Unescape(string text) {
    var result = new StringBuilder(text.Length);
    for (var index = 0; index < text.Length; ++index) {
      if (text[index] != '\\') {
        result.Append(text[index]);
        continue;
      }

      if (++index == text.Length)
        throw new FormatException("a backslash ends the text at position " + (index - 1).ToString(CultureInfo.InvariantCulture));

      result.Append(text[index] switch {
        'n' => '\n',
        't' => '\t',
        '\\' => '\\',
        '"' => '"',
        _ => throw new FormatException($"unknown escape '\\{text[index]}' at position {(index - 1).ToString(CultureInfo.InvariantCulture)}")
      });
    }

    return result.ToString();
  }

}
