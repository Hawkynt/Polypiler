using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;

namespace Polypiler.Core.Frontends.Python.Resolution;

/// <summary>What a name stands for where a function reads it.</summary>
internal enum Binding {

  /// <summary>A parameter or a local of the function.</summary>
  Local,

  /// <summary>A function of the module.</summary>
  Function,

  /// <summary>A built-in of the profile that no scope of the import rebinds.</summary>
  BuiltIn,

  /// <summary>Nothing the import knows.</summary>
  Unknown

}

/// <summary>
/// Binds names as the Python Language Reference ("Naming and binding") does for the subset: a name a
/// function assigns anywhere in its body is local to the whole function, before and after the
/// assignment; otherwise it is a function of the module, otherwise a built-in. A built-in is only the
/// built-in where neither the function nor the module rebinds its name.
/// </summary>
internal static class ScopeResolver {

  /// <summary>The built-ins the import knows, by their names.</summary>
  public static readonly IReadOnlySet<string> BuiltIns = new HashSet<string> { "int", "float", "bool", "abs", "round", "divmod", "pow", "range" };

  /// <summary>Collects the locals of a function: every name a statement of its body binds, in the order of the first binding.</summary>
  public static void CollectLocals(FunctionModel function) {
    foreach (var statement in function.Body)
      Collect(function, statement);
  }

  /// <summary>What a name stands for in a function of a module.</summary>
  public static Binding Resolve(FunctionModel function, IReadOnlyDictionary<string, FunctionModel> functions, string name)
    => function.Binds(name) ? Binding.Local
      : functions.ContainsKey(name) ? Binding.Function
      : BuiltIns.Contains(name) ? Binding.BuiltIn
      : Binding.Unknown;

  /// <summary>The names an assignment target binds, in order: a name, or the names of a tuple or list of them.</summary>
  public static IEnumerable<PyNode> Targets(PyNode target) => target.Kind switch {
    "Name" => [target],
    "Tuple" or "List" => target.Nodes("elts").SelectMany(Targets),
    _ => []
  };

  private static void Collect(FunctionModel function, PyNode statement) {
    switch (statement.Kind) {
      case "Assign":
        foreach (var target in statement.Nodes("targets").SelectMany(Targets))
          Bind(function, target);

        break;
      case "AugAssign":
      case "AnnAssign" when statement.Node("value") is not null:
        foreach (var target in Targets(statement.Node("target")!))
          Bind(function, target);

        break;
      case "For":
        foreach (var target in Targets(statement.Node("target")!))
          Bind(function, target);

        break;
      case "Match":
        foreach (var matchCase in statement.Nodes("cases"))
          foreach (var capture in Captures(matchCase.Node("pattern")!))
            Bind(function, capture);

        break;
    }

    foreach (var field in new[] { "body", "orelse" })
      foreach (var inner in statement.Nodes(field))
        Collect(function, inner);

    foreach (var matchCase in statement.Nodes("cases"))
      foreach (var inner in matchCase.Nodes("body"))
        Collect(function, inner);
  }

  /// <summary>The patterns of a <c>case</c> that bind a name: captures, also inside alternatives and <c>as</c>.</summary>
  public static IEnumerable<PyNode> Captures(PyNode pattern) {
    if (pattern.Is("MatchAs") && pattern.Text("name") is not null)
      yield return pattern;

    foreach (var inner in pattern.Nodes("patterns"))
      foreach (var capture in Captures(inner))
        yield return capture;

    if (pattern.Node("pattern") is { } nested)
      foreach (var capture in Captures(nested))
        yield return capture;
  }

  private static void Bind(FunctionModel function, PyNode node) {
    var name = node.Text("id") ?? node.Text("name")!;
    if (!function.Binds(name))
      function.Locals[name] = new Variable(name, node);
  }

}
