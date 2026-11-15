using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;

namespace Polypiler.Core.Frontends.Python.Resolution;

/// <summary>
/// Requires every local to be assigned on every path before it is read. Where CPython would raise
/// <c>UnboundLocalError</c> on some path - a name assigned in one arm of an <c>if</c> only, a loop
/// variable after a loop that may not have run - the import is refused instead of modelling the
/// error. The analysis also finds whether control reaches the end of the body.
/// </summary>
/// <remarks>
/// A state is the set of locals assigned on every path that reaches a point, or
/// <see langword="null"/> where no path reaches it. A loop's body is analysed from the state before
/// the loop, which is what its first pass sees and what every later pass extends.
/// </remarks>
internal sealed class DefiniteAssignment(SourceFile file, FunctionModel function, List<Diagnostic> diagnostics) {

  private readonly Stack<Loop> _loops = new();
  private readonly HashSet<PyNode> _reported = [];

  private sealed class Loop {

    public List<HashSet<string>> Breaks { get; } = [];

    public List<HashSet<string>> Continues { get; } = [];

  }

  /// <summary>Analyses the body; sets <see cref="FunctionModel.EndReachable"/>.</summary>
  public void Run() {
    var end = this.Block(function.Body, []);
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
        return Assigned(state, statement.Nodes("targets").SelectMany(ScopeResolver.Targets));
      case "AugAssign":
        this.Read(statement.Node("target")!, state);
        this.Reads(statement.Node("value")!, state);
        return Assigned(state, ScopeResolver.Targets(statement.Node("target")!));
      case "AnnAssign":
        if (statement.Node("value") is not { } value)
          return state;

        this.Reads(value, state);
        return Assigned(state, ScopeResolver.Targets(statement.Node("target")!));
      case "Expr" or "Assert":
        this.Reads(statement, state);
        return state;
      case "Return":
        this.Reads(statement, state);
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
    var range = statement.Node("iter")!;
    foreach (var argument in range.Nodes("args"))
      this.Reads(argument, state);

    var entry = Assigned(state, ScopeResolver.Targets(statement.Node("target")!));
    var loop = new Loop();
    this._loops.Push(loop);
    var end = this.Block(statement.Nodes("body"), [.. entry]);
    this._loops.Pop();

    // A range that is not empty runs the body at least once: the loop ends after a pass.
    var exhausted = NotEmpty(range)
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

  /// <summary>Whether a <c>range(...)</c> of constant integer arguments has an element.</summary>
  private static bool NotEmpty(PyNode range) {
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
    => test.Is("Constant") && test.Text("kind") switch {
      "bool" => test.Text("value") == "True",
      "int" => test.Text("value") != "0",
      _ => Constants.Float(test.Text("value")!) != 0
    };

  private static HashSet<string> Assigned(HashSet<string> state, IEnumerable<PyNode> targets) {
    var result = new HashSet<string>(state);
    foreach (var target in targets)
      result.Add(target.Text("id") ?? target.Text("name")!);

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

  /// <summary>Reports a name read where it is a local not assigned on every path to it.</summary>
  private void Read(PyNode name, HashSet<string> state) {
    var id = name.Text("id")!;
    if (function.Locals.ContainsKey(id) && !state.Contains(id) && this._reported.Add(name))
      diagnostics.Add(file.At(name, PythonFrontend.MaybeUnboundDescriptor, $"'{id}' may be unbound here; python would raise UnboundLocalError"));
  }

  /// <summary>Reports every local a node reads that is not assigned on every path to it.</summary>
  private void Reads(PyNode node, HashSet<string> state) {
    // The statements and expressions read here hold no name that is assigned: assignment targets are not read through this.
    if (node.Is("Name")) {
      this.Read(node, state);
      return;
    }

    foreach (var field in new[] { "value", "test", "left", "right", "operand", "body", "orelse", "msg" })
      if (node.Node(field) is { } inner)
        this.Reads(inner, state);

    foreach (var field in new[] { "values", "comparators", "args", "elts" })
      foreach (var inner in node.Nodes(field))
        this.Reads(inner, state);
  }

}
