using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;

namespace Polypiler.Core.Frontends.Python.Resolution;

/// <summary>What a name stands for where a function reads it.</summary>
internal enum Binding {

  /// <summary>A parameter or a local of the function, or a variable of a comprehension or lambda in it.</summary>
  Local,

  /// <summary>A variable of a function the function stands in.</summary>
  Captured,

  /// <summary>A function nested in the function or in one it stands in.</summary>
  Nested,

  /// <summary>A function of the module.</summary>
  Function,

  /// <summary>A class of the module, or a built-in exception.</summary>
  Class,

  /// <summary>A module-level name that holds one value: a constant or a table.</summary>
  Constant,

  /// <summary>A module-level name functions assign through <c>global</c>.</summary>
  Global,

  /// <summary>A name an import binds.</summary>
  Import,

  /// <summary>A built-in of the profile that no scope of the import rebinds.</summary>
  BuiltIn,

  /// <summary>Nothing the import knows.</summary>
  Unknown

}

/// <summary>
/// Binds names as the Python Language Reference ("Naming and binding") does for the subset: a name a
/// function assigns anywhere in its body is local to the whole function, before and after the
/// assignment, unless it declares it <c>nonlocal</c> or <c>global</c>; otherwise it is a variable of
/// a function it stands in, then a name of the module, then a built-in. A comprehension and a lambda
/// have scopes of their own: their variables are bound by the very name nodes inside them.
/// </summary>
internal static class ScopeResolver {

  /// <summary>The built-ins the import knows, by their names.</summary>
  public static readonly IReadOnlySet<string> BuiltIns = new HashSet<string> {
    "int", "float", "bool", "abs", "round", "divmod", "pow", "range", "len", "list", "tuple", "bytes", "bytearray", "str", "dict", "set",
    "min", "max", "sum", "any", "all", "sorted", "reversed", "enumerate", "zip", "map", "filter", "ord", "chr", "isinstance",
    "ValueError", "TypeError", "IndexError", "KeyError", "ZeroDivisionError", "OverflowError", "ArithmeticError", "LookupError",
    "AssertionError", "RuntimeError", "NotImplementedError", "Exception", "BaseException", "UnicodeEncodeError"
  };

  /// <summary>Collects the locals of a function and of the functions nested in it, and binds the names of its comprehensions and lambdas.</summary>
  public static void CollectLocals(FunctionModel function, IDictionary<PyNode, Variable> bound) {
    foreach (var statement in function.Body)
      Collect(function, statement, bound);
  }

  /// <summary>Binds the variables of the comprehensions in the module's own statements.</summary>
  public static void ModuleScopes(ModuleModel module, IDictionary<PyNode, Variable> bound) {
    foreach (var statement in module.Statements.Where(statement => statement.Kind is "Assign" or "AnnAssign" or "AugAssign"))
      foreach (var expression in Expressions(statement))
        Scopes(null, expression, bound);
  }

  /// <summary>What a name stands for in a function of a module.</summary>
  public static Binding Resolve(FunctionModel? function, ModuleModel module, string name, IReadOnlyDictionary<PyNode, Variable>? bound = null, PyNode? node = null) {
    if (node is not null && bound is not null && bound.ContainsKey(node))
      return Binding.Local;

    if (function is not null) {
      if (function.Globals.Contains(name))
        return module.GlobalVariables.ContainsKey(name) ? Binding.Global : Module(module, name);

      if (function.BindsOwn(name))
        return Binding.Local;

      for (var scope = function; scope is not null; scope = scope.Parent)
        if (scope.Nested.ContainsKey(name))
          return Binding.Nested;

      if (function.Find(name) is not null)
        return Binding.Captured;

      for (var scope = function.Parent; scope is not null; scope = scope.Parent)
        if (scope.Globals.Contains(name))
          return Module(module, name);
    }

    return Module(module, name);
  }

  private static Binding Module(ModuleModel module, string name)
    => module.Functions.ContainsKey(name) ? Binding.Function
      : module.Classes.ContainsKey(name) ? Binding.Class
      : Classes.IsBuiltinException(name) && !module.Names.Contains(name) && !module.Imports.ContainsKey(name) ? Binding.Class
      : module.GlobalVariables.ContainsKey(name) ? Binding.Global
      : module.Names.Contains(name) ? Binding.Constant
      : module.Imports.ContainsKey(name) ? Binding.Import
      : BuiltIns.Contains(name) ? Binding.BuiltIn
      : Binding.Unknown;

  /// <summary>The nested function a name stands for in a function, or <see langword="null"/>.</summary>
  public static FunctionModel? NestedFunction(FunctionModel function, string name) {
    for (var scope = function; scope is not null; scope = scope.Parent)
      if (scope.Nested.TryGetValue(name, out var nested))
        return nested;

    return null;
  }

  /// <summary>The names an assignment target binds, in order: a name, or the names of a tuple or list of them.</summary>
  public static IEnumerable<PyNode> Targets(PyNode target) => target.Kind switch {
    "Name" => [target],
    "Tuple" or "List" => target.Nodes("elts").SelectMany(Targets),
    _ => []
  };

  private static void Collect(FunctionModel function, PyNode statement, IDictionary<PyNode, Variable> bound) {
    switch (statement.Kind) {
      case "Assign":
        foreach (var target in statement.Nodes("targets").SelectMany(Targets))
          Bind(function, target);

        break;
      case "AugAssign":
      case "AnnAssign" when statement.Node("value") is not null:
      case "For":
        foreach (var target in Targets(statement.Node("target")!))
          Bind(function, target);

        break;
      case "Match":
        foreach (var matchCase in statement.Nodes("cases"))
          foreach (var capture in Captures(matchCase.Node("pattern")!))
            Bind(function, capture);

        break;
      case "Try":
        foreach (var handler in statement.Nodes("handlers").Where(handler => handler.Text("name") is not null))
          Bind(function, handler);

        break;
      case "Global":
        foreach (var name in statement.Texts("names"))
          function.Globals.Add(name);

        break;
      case "Nonlocal":
        foreach (var name in statement.Texts("names"))
          function.Nonlocals.Add(name);

        break;
      case "FunctionDef":
        if (!function.Nested.ContainsKey(statement.Text("name")!)) {
          var nested = new FunctionModel(statement, null, function);
          function.Nested[nested.Name] = nested;
          CollectLocals(nested, bound);
        }

        return;
    }

    foreach (var expression in Expressions(statement))
      Scopes(function, expression, bound);

    foreach (var field in new[] { "body", "orelse", "finalbody" })
      foreach (var inner in statement.Nodes(field))
        Collect(function, inner, bound);

    foreach (var handler in statement.Nodes("handlers"))
      foreach (var inner in handler.Nodes("body"))
        Collect(function, inner, bound);

    foreach (var matchCase in statement.Nodes("cases"))
      foreach (var inner in matchCase.Nodes("body"))
        Collect(function, inner, bound);
  }

  /// <summary>The expressions a statement evaluates itself, not those of the statements in its blocks.</summary>
  public static IEnumerable<PyNode> Expressions(PyNode statement) {
    foreach (var field in new[] { "value", "test", "iter", "subject", "msg", "exc", "target" })
      if (statement.Node(field) is { } expression)
        yield return expression;

    foreach (var target in statement.Nodes("targets"))
      yield return target;

    foreach (var matchCase in statement.Nodes("cases"))
      if (matchCase.Node("guard") is { } guard)
        yield return guard;
  }

  /// <summary>Every node inside an expression, the expression first, without descending into lambdas and comprehensions unless asked.</summary>
  public static IEnumerable<PyNode> Walk(PyNode expression, bool scopes = false) {
    yield return expression;
    if (!scopes && expression.Kind is "Lambda" or "ListComp" or "GeneratorExp" or "SetComp" or "DictComp")
      yield break;

    foreach (var field in new[] { "value", "left", "right", "operand", "test", "body", "orelse", "func", "slice", "lower", "upper", "step", "elt", "key", "target", "iter" })
      if (expression.Node(field) is { } inner)
        foreach (var node in Walk(inner, scopes))
          yield return node;

    foreach (var field in new[] { "values", "comparators", "args", "elts", "keywords", "generators", "ifs", "keys", "defaults", "kw_defaults" })
      foreach (var inner in expression.Nodes(field))
        foreach (var node in Walk(inner, scopes))
          yield return node;

    if (expression.Node("args") is { Kind: "arguments" } arguments)
      foreach (var inner in arguments.Nodes("defaults"))
        foreach (var node in Walk(inner, scopes))
          yield return node;
  }

  /// <summary>
  /// The scopes inside an expression: the variables of every comprehension and lambda, each bound by
  /// the name nodes that read it, and the targets of <c>:=</c>, which bind in the function.
  /// </summary>
  private static void Scopes(FunctionModel? function, PyNode expression, IDictionary<PyNode, Variable> bound) {
    foreach (var node in Walk(expression)) {
      switch (node.Kind) {
        case "NamedExpr" when function is not null:
          Bind(function, node.Node("target")!);
          break;
        case "ListComp" or "GeneratorExp" or "SetComp" or "DictComp":
          Comprehension(function, node, bound, []);
          break;
        case "Lambda":
          Lambda(function, node, bound, []);
          break;
      }
    }
  }

  /// <summary>
  /// A comprehension: its first iterable is evaluated in the enclosing scope, every later part in its
  /// own, where its targets are variables of their own. A <c>:=</c> inside it binds in the function.
  /// </summary>
  private static void Comprehension(FunctionModel? function, PyNode comprehension, IDictionary<PyNode, Variable> bound, Dictionary<string, Variable> outer) {
    var generators = comprehension.Nodes("generators");
    var scope = new Dictionary<string, Variable>(outer);
    for (var index = 0; index < generators.Count; ++index) {
      var generator = generators[index];
      // The first iterable belongs to the enclosing scope.
      Inner(function, generator.Node("iter")!, bound, index == 0 ? outer : scope);
      foreach (var target in Targets(generator.Node("target")!)) {
        var variable = new Variable(target.Text("id")!, target);
        scope[variable.Name] = variable;
        bound[target] = variable;
      }

      foreach (var condition in generator.Nodes("ifs"))
        Inner(function, condition, bound, scope);
    }

    foreach (var field in new[] { "elt", "key", "value" })
      if (comprehension.Node(field) is { } part)
        Inner(function, part, bound, scope);
  }

  private static void Lambda(FunctionModel? function, PyNode lambda, IDictionary<PyNode, Variable> bound, Dictionary<string, Variable> outer) {
    var scope = new Dictionary<string, Variable>(outer);
    foreach (var argument in lambda.Node("args")!.Nodes("args")) {
      var variable = new Variable(argument.Text("arg")!, argument);
      scope[variable.Name] = variable;
      bound[argument] = variable;
    }

    Inner(function, lambda.Node("body")!, bound, scope);
  }

  /// <summary>Binds the names of an expression inside a comprehension or lambda to the variables of its scope.</summary>
  private static void Inner(FunctionModel? function, PyNode expression, IDictionary<PyNode, Variable> bound, Dictionary<string, Variable> scope) {
    foreach (var node in Walk(expression)) {
      switch (node.Kind) {
        case "Name" when scope.TryGetValue(node.Text("id")!, out var variable):
          bound[node] = variable;
          break;
        case "NamedExpr" when function is not null:
          Bind(function, node.Node("target")!);
          break;
        case "ListComp" or "GeneratorExp" or "SetComp" or "DictComp":
          Comprehension(function, node, bound, scope);
          break;
        case "Lambda":
          Lambda(function, node, bound, scope);
          break;
      }
    }
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
    if (!function.Nonlocals.Contains(name) && !function.Globals.Contains(name) && function.Own(name) is null)
      function.Locals[name] = new Variable(name, node);
  }

}
