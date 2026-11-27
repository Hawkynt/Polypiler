using System;
using System.Collections.Generic;
using System.Globalization;

namespace Polypiler.Core.Frontends.Go.Syntax;

/// <summary>
/// The Go version a <c>//go:build</c> expression implies (<c>constraint.GoVersion</c> of the Go
/// distribution, BSD-3-Clause, see <c>THIRD-PARTY-NOTICE.Go.txt</c>): the minimum version every way of
/// satisfying it needs - the larger of the two sides of <c>&amp;&amp;</c>, the smaller of the two of
/// <c>||</c>, none under <c>!</c> or for any other tag.
/// </summary>
internal static class GoBuildConstraint {

  /// <summary>The minor version of Go 1 an expression implies, as text, or <see langword="null"/> when it implies none or is no expression.</summary>
  public static string? MinimumMinor(string? expression) {
    if (expression is null)
      return null;

    var tokens = Tokens(expression);
    var position = 0;
    int? Or() {
      var left = And();
      while (position < tokens.Count && tokens[position] == "||") {
        ++position;
        var right = And();
        left = left is { } a && right is { } b ? Math.Min(a, b) : null;
      }

      return left;
    }

    int? And() {
      var left = Not();
      while (position < tokens.Count && tokens[position] == "&&") {
        ++position;
        var right = Not();
        left = left is { } a ? right is { } b ? Math.Max(a, b) : a : right;
      }

      return left;
    }

    int? Not() {
      if (position >= tokens.Count)
        return null;

      var token = tokens[position++];
      switch (token) {
        case "!":
          Not();
          return null;
        case "(":
          var inner = Or();
          if (position < tokens.Count && tokens[position] == ")")
            ++position;

          return inner;
        default:
          return token.StartsWith("go1.", StringComparison.Ordinal) && int.TryParse(token.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            ? minor
            : null;
      }
    }

    return Or()?.ToString(CultureInfo.InvariantCulture);
  }

  /// <summary>The operators <c>!</c>, <c>&amp;&amp;</c>, <c>||</c>, the parentheses and the tags of an expression.</summary>
  private static List<string> Tokens(string expression) {
    var tokens = new List<string>();
    for (var index = 0; index < expression.Length;) {
      var character = expression[index];
      if (char.IsWhiteSpace(character)) {
        ++index;
      } else if (character is '!' or '(' or ')') {
        tokens.Add(character.ToString());
        ++index;
      } else if (character is '&' or '|') {
        var length = index + 1 < expression.Length && expression[index + 1] == character ? 2 : 1;
        tokens.Add(expression.Substring(index, length));
        index += length;
      } else {
        var start = index;
        while (index < expression.Length && !char.IsWhiteSpace(expression[index]) && expression[index] is not ('!' or '(' or ')' or '&' or '|'))
          ++index;

        tokens.Add(expression[start..index]);
      }
    }

    return tokens;
  }

}
