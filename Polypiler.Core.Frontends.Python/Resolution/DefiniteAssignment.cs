using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;

namespace Polypiler.Core.Frontends.Python.Resolution;

/// <summary>
/// Requires every local to be assigned on every path before it is read. Where CPython would raise
/// <c>UnboundLocalError</c> on some path - a name assigned in one arm of an <c>if</c> only, a loop
/// variable after a loop that may not have run - the import is refused instead of modelling the
/// error. A nested function may read a variable of the function it stands in only where that variable
/// is assigned on every path to its <c>def</c>, and may be called only after it. The analysis also
/// finds whether control reaches the end of the body.
/// </summary>
/// <remarks>
/// A state is the set of locals assigned on every path that reaches a point, or
/// <see langword="null"/> where no path reaches it. A loop's body is analysed from the state before
/// the loop, which is what its first pass sees and what every later pass extends.
/// </remarks>
internal sealed class DefiniteAssignment(SourceFile file, FunctionModel function, IReadOnlyDictionary<PyNode, Variable> bound, List<Diagnostic> diagnostics) {

  private readonly Stack<Loop> _loops = new();
  private readonly HashSet<PyNode> _reported = [];

  private sealed class Loop {

    public List<HashSet<string>> Breaks { get; } = [];

    public List<HashSet<string>> Continues { get; } = [];

  }

  /// <summary>Analyses the body; sets <see cref="FunctionModel.EndReachable"/>.</summary>
  public void Run() {
    var end = this.Block(function.Body, [.. function.Parameters.Select(parameter => parameter.Name)]);
    function.EndReachable = end is not null;
  }

  private HashSet<string>? Block(IReadOnlyList<PyNode> statements, HashSet<string>? state) {
    foreach (var statement in statements)
      state = this.Statement(statement, state);

    return state;
  }

  private HashSet<string>? Statement(PyNode statement, HashSet<string>? state) {
    if (state is null)
      return null;

    switch (statement.Kind) {
      case "Assign":
        this.Reads(statement.Node("value")!, state);
        foreach (var target in statement.Nodes("targets"))
          this.TargetReads(target, state);

        return Assigned(state, statement.Nodes("targets").SelectMany(ScopeResolver.Targets));
      case "AugAssign":
        var augmented = statement.Node("target")!;
        if (augmented.Is("Name"))
          this.Read(augmented, state);
        else
          this.TargetReads(augmented, state);

        this.Reads(statement.Node("value")!, state);
        return Assigned(state, ScopeResolver.Targets(augmented));
      case "AnnAssign":
        if (statement.Node("value") is not { } value)
          return state;

        this.Reads(value, state);
        this.TargetReads(statement.Node("target")!, state);
        return Assigned(state, ScopeResolver.Targets(statement.Node("target")!));
      case "Expr" or "Assert":
        foreach (var field in new[] { "value", "test", "msg" })
          if (statement.Node(field) is { } expression)
            this.Reads(expression, state);

        return state;
      case "Return":
        if (statement.Node("value") is { } returned)
          this.Reads(returned, state);

        return null;
      case "Raise":
        if (statement.Node("exc") is { } raised)
          this.Reads(raised, state);

        return null;
      case "If":
        this.Reads(statement.Node("test")!, state);
        return Meet(this.Block(statement.Nodes("body"), [.. state]), this.Block(statement.Nodes("orelse"), [.. state]));
      case "While":
        return this.While(statement, state);
      case "For":
        return this.For(statement, state);
      case "Match":
        return this.Match(statement, state);
      case "Try":
        return this.Try(statement, state);
      case "FunctionDef":
        this.Nested(statement, state);
        return Assigned(state, [], statement.Text("name"));
      case "Break":
        this._loops.Peek().Breaks.Add(state);
        return null;
      case "Continue":
        this._loops.Peek().Continues.Add(state);
        return null;
      default:
        return state;
    }
  }

  /// <summary>The reads of an assignment target that is no name: the object of an attribute, the sequence and the index of an element.</summary>
  private void TargetReads(PyNode target, HashSet<string> state) {
    switch (target.Kind) {
      case "Attribute":
        this.Reads(target.Node("value")!, state);
        break;
      case "Subscript":
        this.Reads(target.Node("value")!, state);
        this.Reads(target.Node("slice")!, state);
        break;
    }
  }

  private HashSet<string>? While(PyNode statement, HashSet<string> state) {
    var test = statement.Node("test")!;
    this.Reads(test, state);
    var loop = new Loop();
    this._loops.Push(loop);
    this.Block(statement.Nodes("body"), [.. state]);
    this._loops.Pop();

    // The test can be false the first time it is evaluated, unless it is a constant that is true.
    var exhausted = AlwaysTrue(test) ? null : state;
    var afterElse = this.Block(statement.Nodes("orelse"), exhausted is null ? null : [.. exhausted]);
    return loop.Breaks.Aggregate(afterElse, Meet);
  }

  private HashSet<string>? For(PyNode statement, HashSet<string> state) {
    var iterable = statement.Node("iter")!;
    this.Reads(iterable, state);
    var entry = Assigned(state, ScopeResolver.Targets(statement.Node("target")!));
    var loop = new Loop();
    this._loops.Push(loop);
    var end = this.Block(statement.Nodes("body"), [.. entry]);
    this._loops.Pop();

    // A range that is not empty runs the body at least once: the loop ends after a pass.
    var exhausted = NotEmpty(iterable)
      ? loop.Continues.Aggregate(end, Meet) is { } passed ? new HashSet<string>(passed) : null
      : state;
    var afterElse = this.Block(statement.Nodes("orelse"), exhausted is null ? null : [.. exhausted]);
    return loop.Breaks.Aggregate(afterElse, Meet);
  }

  private HashSet<string>? Match(PyNode statement, HashSet<string> state) {
    this.Reads(statement.Node("subject")!, state);
    HashSet<string>? result = null;
    var fallsThrough = true;
    foreach (var matchCase in statement.Nodes("cases")) {
      var pattern = matchCase.Node("pattern")!;
      var inCase = Assigned(state, ScopeResolver.Captures(pattern));
      if (matchCase.Node("guard") is { } guard)
        this.Reads(guard, inCase);

      result = Meet(result, this.Block(matchCase.Nodes("body"), [.. inCase]));
      if (pattern.Is("MatchAs") && pattern.Node("pattern") is null && matchCase.Node("guard") is null)
        fallsThrough = false;
    }

    return fallsThrough ? Meet(result, state) : result;
  }

  /// <summary>
  /// <c>try</c>: a handler may start anywhere in the body, so it sees what was assigned before the
  /// <c>try</c> only; <c>else</c> follows the body; after the statement holds what every way through it
  /// assigned; <c>finally</c> runs from the state before the <c>try</c>, and what it assigns holds after.
  /// </summary>
  private HashSet<string>? Try(PyNode statement, HashSet<string> state) {
    var afterBody = this.Block(statement.Nodes("body"), [.. state]);
    var afterElse = this.Block(statement.Nodes("orelse"), afterBody is null ? null : [.. afterBody]);
    var result = afterElse;
    foreach (var handler in statement.Nodes("handlers")) {
      var entry = handler.Text("name") is { } name ? Assigned(state, [], name) : [.. state];
      // Python deletes the name of the exception at the end of the handler.
      var afterHandler = this.Block(handler.Nodes("body"), entry);
      if (afterHandler is not null && handler.Text("name") is { } caught && !state.Contains(caught))
        afterHandler.Remove(caught);

      result = Meet(result, afterHandler);
    }

    var final = statement.Nodes("finalbody");
    if (final.Count == 0)
      return result;

    var afterFinal = this.Block(final, [.. state]);
    if (afterFinal is null || result is null)
      return null;

    result.UnionWith(afterFinal);
    return result;
  }

  /// <summary>A nested <c>def</c>: every variable of this function it reads must be assigned where the <c>def</c> runs.</summary>
  private void Nested(PyNode statement, HashSet<string> state) {
    var nested = function.Nested[statement.Text("name")!];
    foreach (var name in Free(nested).Where(name => name.Text("id") is { } id && nested.Find(id) is { } variable && ReferenceEquals(function.Own(id), variable)))
      if (!state.Contains(name.Text("id")!) && this._reported.Add(name))
        diagnostics.Add(file.At(name, PythonFrontend.MaybeUnboundDescriptor, $"'{name.Text("id")}' may be unbound where '{nested.Name}' is defined; python would raise NameError where it reads it"));
  }

  /// <summary>The names a nested function and the functions nested in it read and do not bind themselves.</summary>
  private IEnumerable<PyNode> Free(FunctionModel nested) {
    IEnumerable<PyNode> Nodes(PyNode statement)
      => ScopeResolver.Expressions(statement).SelectMany(expression => ScopeResolver.Walk(expression, true))
        .Concat(new[] { "body", "orelse", "finalbody" }.SelectMany(field => statement.Nodes(field)).Concat(statement.Nodes("handlers").SelectMany(handler => handler.Nodes("body")))
          .Concat(statement.Nodes("cases").SelectMany(matchCase => matchCase.Nodes("body"))).SelectMany(Nodes));
    return nested.Body.SelectMany(Nodes).Where(node => node.Is("Name") && !bound.ContainsKey(node) && !nested.BindsOwn(node.Text("id")!));
  }

  /// <summary>Whether a <c>range(...)</c> of constant integer arguments has an element.</summary>
  private static bool NotEmpty(PyNode range) {
    if (!range.Is("Call") || range.Node("func") is not { Kind: "Name" } callee || callee.Text("id") != "range")
      return false;

    var values = new List<System.Numerics.BigInteger>();
    foreach (var argument in range.Nodes("args")) {
      if (Constants.Integer(argument) is not { } value)
        return false;

      values.Add(value);
    }

    var (start, stop, step) = values.Count switch {
      1 => (System.Numerics.BigInteger.Zero, values[0], System.Numerics.BigInteger.One),
      2 => (values[0], values[1], System.Numerics.BigInteger.One),
      _ => (values[0], values[1], values[2])
    };
    return step > 0 ? start < stop : step < 0 && start > stop;
  }

  /// <summary>Whether a condition is a constant that is true: <c>while True</c>, <c>while 1</c>.</summary>
  private static bool AlwaysTrue(PyNode test)
    => test.Is("Constant") && test.ConstantKind switch {
      "bool" => test.Value is true,
      "int" => !((System.Numerics.BigInteger)test.Value!).IsZero,
      "float" => (double)test.Value! != 0,
      _ => false
    };

  private static HashSet<string> Assigned(HashSet<string> state, IEnumerable<PyNode> targets, string? name = null) {
    var result = new HashSet<string>(state);
    foreach (var target in targets)
      result.Add(target.Text("id") ?? target.Text("name")!);

    if (name is not null)
      result.Add(name);

    return result;
  }

  private static HashSet<string>? Meet(HashSet<string>? first, HashSet<string>? second) {
    if (first is null)
      return second;

    if (second is null)
      return first;

    var result = new HashSet<string>(first);
    result.IntersectWith(second);
    return result;
  }

  /// <summary>Reports a name read where it is a local or a nested function not assigned on every path to it.</summary>
  private void Read(PyNode name, HashSet<string> state) {
    var id = name.Text("id")!;
    if (bound.ContainsKey(name) || function.Nonlocals.Contains(id) || function.Globals.Contains(id))
      return;

    if ((function.Locals.ContainsKey(id) || function.Nested.ContainsKey(id)) && !state.Contains(id) && this._reported.Add(name))
      diagnostics.Add(file.At(name, PythonFrontend.MaybeUnboundDescriptor, $"'{id}' may be unbound here; python would raise UnboundLocalError"));
  }

  /// <summary>
  /// Reports every local an expression reads that is not assigned on every path to it, in the order
  /// it is evaluated; a <c>:=</c> assigns its name for what follows, except inside an operand that is
  /// evaluated only under a condition, a comprehension or a lambda.
  /// </summary>
  private void Reads(PyNode node, HashSet<string> state) {
    switch (node.Kind) {
      case "Name":
        this.Read(node, state);
        return;
      case "NamedExpr":
        this.Reads(node.Node("value")!, state);
        state.Add(node.Node("target")!.Text("id")!);
        return;
      case "BoolOp":
        var values = node.Nodes("values");
        this.Reads(values[0], state);
        foreach (var value in values.Skip(1))
          this.Reads(value, [.. state]);

        return;
      case "IfExp":
        this.Reads(node.Node("test")!, state);
        this.Reads(node.Node("body")!, [.. state]);
        this.Reads(node.Node("orelse")!, [.. state]);
        return;
      case "Lambda":
        this.Reads(node.Node("body")!, [.. state]);
        return;
      case "ListComp" or "GeneratorExp" or "SetComp" or "DictComp":
        var generators = node.Nodes("generators");
        if (generators.Count > 0)
          this.Reads(generators[0].Node("iter")!, state);

        var inner = new HashSet<string>(state);
        foreach (var (generator, index) in generators.Select((generator, index) => (generator, index))) {
          if (index > 0)
            this.Reads(generator.Node("iter")!, inner);

          foreach (var condition in generator.Nodes("ifs"))
            this.Reads(condition, inner);
        }

        if (node.Node("elt") is { } element)
          this.Reads(element, inner);

        return;
    }

    foreach (var field in new[] { "value", "test", "left", "right", "operand", "body", "orelse", "msg", "func", "slice", "lower", "upper", "step", "exc" })
      if (node.Node(field) is { } child)
        this.Reads(child, state);

    foreach (var field in new[] { "values", "comparators", "args", "elts", "keywords" })
      foreach (var child in node.Nodes(field))
        this.Reads(child, state);
  }

}
