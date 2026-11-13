using System;
using System.Linq;

namespace Polypiler.Tests.Backends.CSharp;

/// <summary>
/// Reads the text of a generated class the way the spelling tests need it: the statements of one
/// function, whichever of the two forms of a body the language version of the profile gave it.
/// </summary>
internal static class EmittedCSharp {

  /// <summary>
  /// The statements of a function, one per line, without indentation and without the
  /// <c>unchecked</c> around them. A body that is one expression — <c>=&gt; expression;</c> from C# 6
  /// on — reads as the <c>return</c> statement it is short for.
  /// </summary>
  /// <param name="text">The generated file.</param>
  /// <param name="function">The name of the function.</param>
  public static string[] Body(string text, string function = "F") {
    var lines = text.Split('\n');
    var start = Array.FindIndex(lines, line => line.Contains($" {function}("));
    var arrow = lines[start].IndexOf(") => ", StringComparison.Ordinal);
    if (arrow >= 0) {
      var expression = lines[start][(arrow + 5)..^1];
      return [$"return {(IsUnchecked(text, function) ? expression["unchecked(".Length..^1] : expression)};"];
    }

    var body = lines.Skip(start + 1).TakeWhile(line => line != "  }").Select(line => line.Trim()).ToArray();
    return body is ["unchecked {", .., "}"] ? body[1..^1] : body;
  }

  /// <summary>
  /// The generated file with every body that is one expression written out as the block it is short
  /// for, so that a test can pin how an expression is spelled whatever form its function takes.
  /// </summary>
  /// <param name="text">The generated file.</param>
  public static string WithStatementBodies(string text) => string.Join('\n', text.Split('\n').SelectMany(line => {
    var arrow = line.IndexOf(") => ", StringComparison.Ordinal);
    if (arrow < 0)
      return new[] { line };

    var head = line[..(arrow + 1)] + " {";
    var expression = line[(arrow + 5)..^1];
    return expression.StartsWith("unchecked(", StringComparison.Ordinal)
      ? new[] { head, "    unchecked {", $"      return {expression["unchecked(".Length..^1]};", "    }", "  }" }
      : new[] { head, $"    return {expression};", "  }" };
  }));

  /// <summary>Whether the whole body of a function stands in an <c>unchecked</c> context.</summary>
  /// <param name="text">The generated file.</param>
  /// <param name="function">The name of the function.</param>
  public static bool IsUnchecked(string text, string function = "F") {
    var lines = text.Split('\n');
    var start = Array.FindIndex(lines, line => line.Contains($" {function}("));
    return lines[start].Contains(") => unchecked(", StringComparison.Ordinal) || lines[start + 1] == "    unchecked {";
  }

}
