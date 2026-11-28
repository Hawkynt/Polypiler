using Polypiler.Core.Frontends.Python.Ast;

namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>
/// The <c>from __future__ import</c> statements at the start of a module as <c>ast.parse</c> checks
/// them (<c>Python/future.c</c>): an unknown feature, and <c>braces</c>, are errors.
/// </summary>
internal static class PyFuture {

  private static readonly string[] _features = [
    "nested_scopes", "generators", "division", "absolute_import", "with_statement", "print_function", "unicode_literals", "barry_as_FLUFL",
    "generator_stop", "annotations"];

  /// <summary>Whether a module turns on <c>from __future__ import annotations</c>.</summary>
  public static bool Annotations(PyNode module) {
    foreach (var statement in Leading(module))
      foreach (var alias in statement.Nodes("names"))
        if (alias.Text("name") == "annotations")
          return true;

    return false;
  }

  /// <summary>The future statements a module starts with, after its docstring.</summary>
  private static System.Collections.Generic.IEnumerable<PyNode> Leading(PyNode module) {
    var body = module.Nodes("body");
    var i = body.Count > 0 && IsDocstring(body[0]) ? 1 : 0;
    for (; i < body.Count; i++) {
      var statement = body[i];
      if (!statement.Is("ImportFrom") || statement.Number("level") != 0 || statement.Text("module") != "__future__")
        yield break;

      yield return statement;
    }
  }

  /// <summary>Whether a statement is a docstring: an expression statement of a str constant.</summary>
  public static bool IsDocstring(PyNode statement) => statement.Is("Expr") && statement.Node("value") is { Kind: "Constant", Value: string };

  /// <summary>Throws CPython's error for a future statement that names no feature.</summary>
  public static void Check(PyNode module) {
    foreach (var statement in Leading(module)) {
      foreach (var alias in statement.Nodes("names")) {
        var name = alias.Text("name")!;
        if (System.Array.IndexOf(_features, name) >= 0)
          continue;

        var range = alias.Range!;
        throw new PyError("SyntaxError", name == "braces" ? "not a chance" : $"future feature {name} is not defined", range.Line, range.Column, range.EndLine, range.EndColumn);
      }
    }
  }

}
