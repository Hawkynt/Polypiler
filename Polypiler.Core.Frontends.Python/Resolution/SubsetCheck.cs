using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;

namespace Polypiler.Core.Frontends.Python.Resolution;

/// <summary>
/// Refuses, with the diagnostic of its family, every construct of a function outside the subset the
/// import supports, before any type is inferred: so that what follows sees only constructs it knows.
/// </summary>
internal sealed class SubsetCheck(SourceFile file, IReadOnlyDictionary<string, FunctionModel> functions, List<Diagnostic> diagnostics) {

  private static readonly HashSet<string> _comparisons = ["Eq", "NotEq", "Lt", "LtE", "Gt", "GtE"];

  private static readonly Dictionary<string, string> _operators = new() {
    ["MatMult"] = "@", ["Is"] = "is", ["IsNot"] = "is not", ["In"] = "in", ["NotIn"] = "not in"
  };

  private FunctionModel _function = null!;

  /// <summary>Checks the body of a function; every construct outside the subset adds a diagnostic.</summary>
  public void Check(FunctionModel function) {
    this._function = function;
    foreach (var statement in function.Body)
      this.Statement(statement);
  }

  private void Refuse(PyNode node, DiagnosticDescriptor descriptor, string message) => diagnostics.Add(file.At(node, descriptor, message));

  private void Statement(PyNode statement) {
    switch (statement.Kind) {
      case "Expr":
        // A string or another constant as a statement is a docstring or does nothing.
        if (!statement.Node("value")!.Is("Constant"))
          this.Expression(statement.Node("value")!);

        break;
      case "Return":
        if (statement.Node("value") is { } value && !(value.Is("Constant") && value.Text("kind") == "none"))
          this.Expression(value);

        break;
      case "Assign":
        this.Assign(statement);
        break;
      case "AugAssign":
        this.Target(statement.Node("target")!, false);
        this.Operator(statement, statement.Operator()!);
        this.Expression(statement.Node("value")!);
        break;
      case "AnnAssign":
        this.Target(statement.Node("target")!, false);
        if (statement.Node("value") is { } annotated)
          this.Expression(annotated);

        break;
      case "If":
      case "While":
        this.Expression(statement.Node("test")!);
        this.Statements(statement);
        break;
      case "For":
        this.For(statement);
        break;
      case "Match":
        this.Match(statement);
        break;
      case "Assert":
        this.Expression(statement.Node("test")!);
        if (statement.Node("msg") is { } message && !(message.Is("Constant") && message.Text("kind") == "str"))
          this.Refuse(message, PythonFrontend.UnsupportedStatementDescriptor, "statement 'assert' with a message other than a string literal is not supported by the Python frontend");

        break;
      case "Break" or "Continue" or "Pass":
        break;
      case "FunctionDef" or "AsyncFunctionDef":
        this.Refuse(statement, PythonFrontend.UnsupportedFunctionDescriptor, $"'nested function {statement.Text("name")}' is not supported for functions by the Python frontend");
        break;
      case "Global" or "Nonlocal":
        this.Refuse(statement, PythonFrontend.UnsupportedFunctionDescriptor, $"'{file.Quote(statement)}' is not supported for functions by the Python frontend");
        break;
      default:
        this.Refuse(statement, PythonFrontend.UnsupportedStatementDescriptor, $"statement '{Keyword(statement)}' is not supported by the Python frontend");
        break;
    }
  }

  private void Statements(PyNode statement) {
    foreach (var field in new[] { "body", "orelse" })
      foreach (var inner in statement.Nodes(field))
        this.Statement(inner);
  }

  private static string Keyword(PyNode statement) => statement.Kind switch {
    "TryStar" => "try",
    "Delete" => "del",
    "ImportFrom" => "import",
    "ClassDef" => "class",
    "TypeAlias" => "type",
    _ => statement.Kind.ToLowerInvariant()
  };

  private void Assign(PyNode statement) {
    var targets = statement.Nodes("targets");
    var value = statement.Node("value")!;
    if (targets is [{ Kind: "Tuple" or "List" } tuple]) {
      var names = tuple.Nodes("elts");
      foreach (var element in names)
        this.Target(element, false);

      if (value is { Kind: "Tuple" or "List" } && value.Nodes("elts").Count == names.Count) {
        foreach (var element in value.Nodes("elts"))
          this.Expression(element);
      } else if (this.IsBuiltInCall(value, "divmod") && names.Count == 2) {
        this.Arguments(value, 2);
      } else {
        this.Refuse(statement, PythonFrontend.UnsupportedStatementDescriptor, "statement 'tuple assignment whose right side is no tuple of as many values' is not supported by the Python frontend");
      }

      return;
    }

    foreach (var target in targets)
      this.Target(target, false);

    this.Expression(value);
  }

  private void Target(PyNode target, bool loop) {
    if (target.Is("Name"))
      return;

    var what = target.Kind switch {
      "Starred" => "starred assignment target",
      "Tuple" or "List" => loop ? "a tuple as loop variable" : "nested tuple assignment",
      "Subscript" => "assignment to an element",
      _ => "assignment to an attribute"
    };
    this.Refuse(target, PythonFrontend.UnsupportedStatementDescriptor, $"statement '{what}' is not supported by the Python frontend");
  }

  private void For(PyNode statement) {
    this.Target(statement.Node("target")!, true);
    var iterable = statement.Node("iter")!;
    if (this.IsBuiltInCall(iterable, "range")) {
      var count = iterable.Nodes("args").Count;
      if (count is < 1 or > 3 || iterable.Nodes("keywords").Count > 0)
        this.Refuse(iterable, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{file.Quote(iterable)}' is not supported");
      else
        this.Arguments(iterable, count);
    } else {
      this.Refuse(iterable, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{file.Quote(iterable)}' is not supported");
    }

    this.Statements(statement);
  }

  private void Match(PyNode statement) {
    this.Expression(statement.Node("subject")!);
    foreach (var matchCase in statement.Nodes("cases")) {
      this.Pattern(matchCase.Node("pattern")!, true);
      if (matchCase.Node("guard") is { } guard)
        this.Expression(guard);

      foreach (var inner in matchCase.Nodes("body"))
        this.Statement(inner);
    }
  }

  /// <summary>Checks a pattern: literals, alternatives of literals, a capture, the wildcard.</summary>
  private void Pattern(PyNode pattern, bool top) {
    switch (pattern.Kind) {
      case "MatchValue" when Literal(pattern.Node("value")!):
      case "MatchSingleton":
        return;
      case "MatchAs" when pattern.Node("pattern") is null && (top || pattern.Text("name") is null):
        return;
      case "MatchOr":
        foreach (var alternative in pattern.Nodes("patterns"))
          this.Pattern(alternative, false);

        return;
      default:
        this.Refuse(pattern, PythonFrontend.UnsupportedPatternDescriptor, $"match pattern '{file.Quote(pattern)}' is not supported; supported are literals, '|', captures, '_' and guards");
        return;
    }
  }

  /// <summary>Whether a value pattern is a number literal, possibly negated.</summary>
  private static bool Literal(PyNode value) {
    if (value.Is("UnaryOp") && value.Operator() == "USub")
      value = value.Node("operand")!;

    return value.Is("Constant") && value.Text("kind") is "int" or "float";
  }

  private bool IsBuiltInCall(PyNode node, string name)
    => node.Is("Call") && node.Node("func") is { Kind: "Name" } callee && callee.Text("id") == name
      && ScopeResolver.Resolve(this._function, functions, name) == Binding.BuiltIn;

  private void Arguments(PyNode call, int count) {
    foreach (var argument in call.Nodes("args").Take(count))
      this.Expression(argument);
  }

  private void Operator(PyNode node, string op) {
    if (_operators.TryGetValue(op, out var text))
      this.Refuse(node, PythonFrontend.UnsupportedDescriptor, $"'{text}' is not supported by the Python frontend");
  }

  private void Expression(PyNode expression) {
    switch (expression.Kind) {
      case "Constant":
        var kind = expression.Text("kind");
        if (kind is not ("int" or "float" or "bool"))
          this.Refuse(expression, PythonFrontend.UnsupportedDescriptor, $"'{file.Quote(expression)}' is not supported by the Python frontend");

        break;
      case "Name":
        this.Name(expression, false);
        break;
      case "BinOp":
        this.Expression(expression.Node("left")!);
        this.Operator(expression, expression.Operator()!);
        this.Expression(expression.Node("right")!);
        break;
      case "UnaryOp":
        this.Expression(expression.Node("operand")!);
        break;
      case "BoolOp":
        foreach (var value in expression.Nodes("values"))
          this.Expression(value);

        break;
      case "Compare":
        this.Expression(expression.Node("left")!);
        foreach (var (op, right) in expression.Nodes("ops").Zip(expression.Nodes("comparators"))) {
          if (!_comparisons.Contains(op.Kind))
            this.Operator(expression, op.Kind);

          this.Expression(right);
        }

        break;
      case "IfExp":
        this.Expression(expression.Node("test")!);
        this.Expression(expression.Node("body")!);
        this.Expression(expression.Node("orelse")!);
        break;
      case "Call":
        this.Call(expression);
        break;
      case "Lambda":
        this.Refuse(expression, PythonFrontend.EscapingFunctionDescriptor, $"the function value '{file.Quote(expression)}' escapes, or '{file.Quote(expression)}' is a generator or coroutine; only direct calls of the module's functions are imported");
        break;
      case "Yield" or "YieldFrom" or "Await":
        this.Refuse(expression, PythonFrontend.EscapingFunctionDescriptor, $"the function value '{this._function.Name}' escapes, or '{this._function.Name}' is a generator or coroutine; only direct calls of the module's functions are imported");
        break;
      default:
        this.Refuse(expression, PythonFrontend.UnsupportedDescriptor, $"'{file.Quote(expression)}' is not supported by the Python frontend");
        break;
    }
  }

  private void Name(PyNode name, bool called) {
    var id = name.Text("id")!;
    switch (ScopeResolver.Resolve(this._function, functions, id)) {
      case Binding.Local when called:
      case Binding.Function or Binding.BuiltIn when !called:
        this.Refuse(name, PythonFrontend.EscapingFunctionDescriptor, $"the function value '{id}' escapes, or '{id}' is a generator or coroutine; only direct calls of the module's functions are imported");
        break;
      case Binding.Unknown:
        this.Refuse(name, PythonFrontend.UnresolvedDescriptor, $"'{id}' is neither part of this import nor in the Python call map");
        break;
    }
  }

  private void Call(PyNode call) {
    var callee = call.Node("func")!;
    if (!callee.Is("Name")) {
      this.Refuse(callee, PythonFrontend.UnresolvedDescriptor, $"'{file.Quote(callee)}' is neither part of this import nor in the Python call map");
      return;
    }

    var name = callee.Text("id")!;
    this.Name(callee, true);
    var arguments = call.Nodes("args");
    if (call.Nodes("keywords").Count > 0 || arguments.Any(argument => argument.Is("Starred"))) {
      this.Refuse(call, PythonFrontend.UnsupportedFunctionDescriptor, $"'keyword or unpacked arguments in {file.Quote(call)}' is not supported for functions by the Python frontend");
      return;
    }

    var binding = ScopeResolver.Resolve(this._function, functions, name);
    if (binding == Binding.Function && arguments.Count != functions[name].Parameters.Count)
      this.Refuse(call, PythonFrontend.UnsupportedFunctionDescriptor, $"'a call of {name} with {arguments.Count} of its {functions[name].Parameters.Count} arguments' is not supported for functions by the Python frontend");

    if (binding == Binding.BuiltIn && !BuiltInArity(name, arguments.Count))
      this.Refuse(call, PythonFrontend.UnsupportedDescriptor, $"'{file.Quote(call)}' is not supported by the Python frontend");

    foreach (var argument in arguments)
      this.Expression(argument);
  }

  /// <summary>Whether a built-in is supported with a number of arguments; <c>range</c> and <c>divmod</c> only where a statement takes them apart.</summary>
  private static bool BuiltInArity(string name, int count) => name switch {
    "pow" => count == 2,
    "range" or "divmod" => false,
    _ => count == 1
  };

}
