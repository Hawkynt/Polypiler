using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary>Splits the command line of a step into its arguments.</summary>
public static class CommandLine {

  /// <summary>
  /// The arguments of a command line: separated by whitespace, where text between single quotes is
  /// one argument, whitespace included, and the quotes are not part of it. <c>a '' 'b c'</c> is the
  /// three arguments <c>a</c>, the empty string and <c>b c</c>.
  /// </summary>
  /// <exception cref="FormatException">A quote is not closed; the message names its 0-based position.</exception>
  public static IReadOnlyList<string> Split(string line) {
    var arguments = new List<string>();
    var current = new StringBuilder();
    var inArgument = false;
    for (var index = 0; index < line.Length; ++index) {
      var character = line[index];
      if (character == '\'') {
        var end = line.IndexOf('\'', index + 1);
        if (end < 0)
          throw new FormatException("unterminated quote at position " + index.ToString(CultureInfo.InvariantCulture));

        current.Append(line, index + 1, end - index - 1);
        inArgument = true;
        index = end;
      } else if (char.IsWhiteSpace(character)) {
        if (inArgument)
          arguments.Add(current.ToString());

        current.Clear();
        inArgument = false;
      } else {
        current.Append(character);
        inArgument = true;
      }
    }

    if (inArgument)
      arguments.Add(current.ToString());

    return arguments;
  }

}
