using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;

namespace Polypiler.Core.Frontends.Python.Typing;

/// <summary>
/// Gives every parameter, local and result of the module a static type. A parameter has the type of
/// its hint; one without a hint has the type of the arguments the module's own calls pass. A local
/// and a result have the type of the values that reach them, whatever a hint says - a hint is a
/// recorded contract, never trusted. Two number types join to the wider one (a <c>bool</c> is an
/// <c>int</c>, an <c>int</c> converts to a <c>float</c>); <c>None</c> joins with nothing. The rules
/// run to a fixed point, so recursion and calls in any order are typed.
/// </summary>
internal sealed class TypeInference(SourceFile file, IReadOnlyDictionary<string, FunctionModel> functions, List<Diagnostic> diagnostics) {

  private readonly HashSet<string> _conflicts = [];
  private bool _changed;

  /// <summary>Types the module; every failure adds a diagnostic.</summary>
  public void Run() {
    foreach (var function in functions.Values)
      this.Hints(function);

    if (diagnostics.Count > 0)
      return;

    for (var round = 0; round < 64; ++round) {
      this._changed = false;
      foreach (var function in functions.Values)
        this.Infer(function);

      // A result only recursion reaches takes its hint, from which the rest follows.
      if (!this._changed)
        foreach (var function in functions.Values.Where(function => function.Returns == PyType.Unknown && function.ReturnHint is not null))
          this.Merge(function.Name + " result", function.Returns, Hint(function.ReturnHint!)!.Value, function.ReturnHint!, type => function.Returns = type, () => { });

      if (!this._changed)
        break;
    }

    foreach (var function in functions.Values) {
      foreach (var variable in function.Parameters.Concat(function.Locals.Values).Where(variable => variable.Type == PyType.Unknown))
        diagnostics.Add(file.At(variable.Node, PythonFrontend.MissingTypeDescriptor,
          $"the type of '{variable.Name}' in '{function.Name}' is not known: {(function.Parameters.Contains(variable) ? "it has no type hint and no call of the module passes it a value" : "no value of a known type reaches it")}"));

      if (function.Returns == PyType.Unknown)
        diagnostics.Add(file.At(function.Node, PythonFrontend.MissingTypeDescriptor, $"the result type of '{function.Name}' is not known: it has no return type hint and no return value of a known type"));
    }
  }

  /// <summary>The type a hint names, or <see langword="null"/> for a hint the import does not know.</summary>
  public static PyType? Hint(PyNode hint) => hint.Kind switch {
    "Name" => PyTypes.OfHint(hint.Text("id")!),
    "Constant" when hint.Text("kind") == "none" => PyType.None,
    "Constant" when hint.Text("kind") == "str" => PyTypes.OfHint(hint.Text("value")!.Trim()),
    _ => null
  };

  private void Hints(FunctionModel function) {
    foreach (var parameter in function.Parameters.Where(parameter => parameter.Hint is not null)) {
      if (Hint(parameter.Hint!) is { } type and not PyType.None)
        parameter.Type = type;
      else
        diagnostics.Add(file.At(parameter.Hint!, PythonFrontend.MissingTypeDescriptor, $"the type hint '{file.Quote(parameter.Hint!)}' of '{parameter.Name}' is not supported; supported are int, float and bool"));
    }

    if (function.ReturnHint is { } returns && Hint(returns) is null)
      diagnostics.Add(file.At(returns, PythonFrontend.MissingTypeDescriptor, $"the return type hint '{file.Quote(returns)}' of '{function.Name}' is not supported; supported are int, float, bool and None"));
  }

  private void Infer(FunctionModel function) {
    foreach (var statement in function.Body)
      this.Statement(function, statement);

    if (function.EndReachable)
      this.Result(function, PyType.None, function.Node);
  }

  private void Statement(FunctionModel function, PyNode statement) {
    switch (statement.Kind) {
      case "Assign":
        this.Calls(function, statement.Node("value")!);
        this.Assign(function, statement.Nodes("targets"), statement.Node("value")!);
        break;
      case "AnnAssign" when statement.Node("value") is { } value:
        this.Calls(function, value);
        this.Assign(function, [statement.Node("target")!], value);
        break;
      case "AugAssign":
        this.Calls(function, statement.Node("value")!);
        var target = statement.Node("target")!;
        var variable = function.Find(target.Text("id")!)!;
        this.Store(function, variable, ExpressionTypes.Binary(statement.Operator()!, variable.Type, this.Of(function, statement.Node("value")!), statement.Node("value")!), target);
        break;
      case "For":
        foreach (var argument in statement.Node("iter")!.Nodes("args"))
          this.Calls(function, argument);

        this.Store(function, function.Find(statement.Node("target")!.Text("id")!)!, PyType.Int, statement.Node("target")!);
        break;
      case "Match":
        this.Calls(function, statement.Node("subject")!);
        var subject = this.Of(function, statement.Node("subject")!);
        foreach (var matchCase in statement.Nodes("cases")) {
          foreach (var capture in ScopeResolver.Captures(matchCase.Node("pattern")!))
            this.Store(function, function.Find(capture.Text("name")!)!, subject, capture);

          if (matchCase.Node("guard") is { } guard)
            this.Calls(function, guard);
        }

        break;
      case "Return":
        var returned = statement.Node("value");
        if (returned is null || (returned.Is("Constant") && returned.Text("kind") == "none")) {
          this.Result(function, PyType.None, statement);
        } else {
          this.Calls(function, returned);
          this.Result(function, this.Of(function, returned), statement);
        }

        break;
      case "Expr" or "Assert" or "If" or "While":
        foreach (var field in new[] { "value", "test", "msg" })
          if (statement.Node(field) is { } expression)
            this.Calls(function, expression);

        break;
    }

    foreach (var field in new[] { "body", "orelse" })
      foreach (var inner in statement.Nodes(field))
        this.Statement(function, inner);

    foreach (var matchCase in statement.Nodes("cases"))
      foreach (var inner in matchCase.Nodes("body"))
        this.Statement(function, inner);
  }

  private void Assign(FunctionModel function, IReadOnlyList<PyNode> targets, PyNode value) {
    if (targets is [{ Kind: "Tuple" or "List" } tuple]) {
      var names = tuple.Nodes("elts");
      if (value.Is("Call")) {
        // q, r = divmod(a, b): both are of the type // gives.
        var arguments = value.Nodes("args");
        var type = ExpressionTypes.Binary("FloorDiv", this.Of(function, arguments[0]), this.Of(function, arguments[1]), arguments[1]);
        foreach (var name in names)
          this.Store(function, function.Find(name.Text("id")!)!, type, name);
      } else {
        foreach (var (name, element) in names.Zip(value.Nodes("elts")))
          this.Store(function, function.Find(name.Text("id")!)!, this.Of(function, element), name);
      }

      return;
    }

    var assigned = this.Of(function, value);
    foreach (var target in targets)
      this.Store(function, function.Find(target.Text("id")!)!, assigned, target);
  }

  private PyType Of(FunctionModel function, PyNode expression) => ExpressionTypes.Of(expression, function, functions);

  /// <summary>Passes the types of the arguments of every call of a function of the module to its parameters without a hint.</summary>
  private void Calls(FunctionModel function, PyNode expression) {
    if (expression.Is("Call") && expression.Node("func") is { Kind: "Name" } callee
        && ScopeResolver.Resolve(function, functions, callee.Text("id")!) == Binding.Function) {
      var called = functions[callee.Text("id")!];
      foreach (var (parameter, argument) in called.Parameters.Zip(expression.Nodes("args")).Where(pair => pair.First.Hint is null))
        this.Store(called, parameter, this.Of(function, argument), argument);
    }

    foreach (var field in new[] { "left", "right", "operand", "test", "body", "orelse" })
      if (expression.Node(field) is { } inner)
        this.Calls(function, inner);

    foreach (var field in new[] { "values", "comparators", "args", "elts" })
      foreach (var inner in expression.Nodes(field))
        this.Calls(function, inner);
  }

  private void Store(FunctionModel function, Variable variable, PyType type, PyNode at)
    => this.Merge($"{function.Name}.{variable.Name}", variable.Type, type, at, joined => variable.Type = joined, () => variable.Widened = true);

  private void Result(FunctionModel function, PyType type, PyNode at)
    => this.Merge(function.Name + " result", function.Returns, type, at, joined => function.Returns = joined, () => function.ReturnWidened = true);

  private void Merge(string what, PyType current, PyType type, PyNode at, System.Action<PyType> set, System.Action widened) {
    if (type == PyType.Unknown)
      return;

    if (PyTypes.Join(current, type) is not { } joined) {
      if (this._conflicts.Add(what))
        diagnostics.Add(file.At(at, PythonFrontend.MixedKindsDescriptor,
          $"'{what}' holds values of the types {PyTypes.Name(current)} and {PyTypes.Name(type)}, which no type of the import holds both of"));

      return;
    }

    if (joined != type)
      widened();

    if (joined != current) {
      set(joined);
      this._changed = true;
    }
  }

}
