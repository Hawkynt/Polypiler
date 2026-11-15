using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

internal sealed partial class FunctionLowering {

  /// <summary>
  /// <c>for v in range(…)</c>: the arguments evaluated once, in order, before the loop; a zero step
  /// raises <c>ValueError</c> before the first pass. A counter walks the range and the loop variable
  /// is assigned from it at the start of every pass, so that assigning the variable in the body skips
  /// nothing; where the body never assigns it and nothing reads it after the loop, the variable is
  /// the counter. A step of 1 or -1 cannot step beyond the 64 bits from inside the range; any other
  /// step decides whether another element follows on the distance to the end, which is exact in
  /// unsigned 64 bits, so that no element of a range near the limits is lost or added.
  /// </summary>
  private IEnumerable<IrStmt> For(PyNode statement) {
    var range = statement.Node("iter")!;
    var body = statement.Nodes("body");
    var target = statement.Node("target")!.Text("id")!;
    var assigned = Assigned(body);
    var setup = new List<IrStmt>();
    var arguments = new List<IrExpr>();
    foreach (var argument in range.Nodes("args")) {
      var value = this.Value(argument);
      if (value.Type == PyType.Float)
        throw this.Refuse(argument, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{this._file.Quote(range)}' is not supported: a float argument raises TypeError");

      this._notes.UsesInt = true;
      var integer = this.Integer(value).Item1;
      if (!(integer is IrConst || (integer is IrLocalRef local && !assigned.Contains(local.Local.Name) && local.Local.Name != target))) {
        var temporary = this.Fresh(arguments.Count == 0 && range.Nodes("args").Count > 1 ? "start" : arguments.Count == 2 ? "step" : "stop", IrType.I64);
        setup.Add(new IrDeclare(temporary, integer));
        integer = Ref(temporary);
      }

      arguments.Add(integer);
    }

    var (start, stop, step) = arguments.Count switch {
      1 => (Int(0), arguments[0], (IrExpr)Int(1)),
      2 => (arguments[0], arguments[1], Int(1)),
      _ => (arguments[0], arguments[1], arguments[2])
    };
    if (step is IrConst { Value: 0 })
      return [.. setup, Raise(IrExceptionClass.ArgumentException, "range() arg 3 must not be zero")];

    var variable = this._variables[target];
    var direct = !assigned.Contains(target) && !UsedOutside(this._function.Body, statement, target);
    var counter = direct ? variable : this.Fresh("k", IrType.I64);
    if (!direct)
      setup.Add(new IrDeclare(counter, Int(0)));

    if (step is not IrConst)
      setup.Add(If(Equal(step, Int(0)), Raise(IrExceptionClass.ArgumentException, "range() arg 3 must not be zero")));

    setup.Add(Assign(counter, start));
    return this.WithElse(statement, () => {
      var lowered = this.Statements(body);
      if (!direct)
        lowered.Insert(0, Assign(variable, Ref(counter)));

      if (step is IrConst { Value: 1 or -1 } unit) {
        var up = unit.Value == 1;
        var loop = new IrFor(Binary(up ? IrBinaryOp.Less : IrBinaryOp.Greater, Ref(counter), stop), new(lowered),
          Block(Assign(counter, Binary(up ? IrBinaryOp.Add : IrBinaryOp.Subtract, Ref(counter), Int(1)))));
        return [.. setup, loop];
      }

      IrExpr Ascending(IrExpr positive, IrExpr negative) => step is IrConst constant ? constant.Value > 0 ? positive : negative : Choose(Binary(IrBinaryOp.Greater, step, Int(0)), positive, negative);
      IrExpr Distance(IrExpr from, IrExpr to) => Convert(IrType.U64, Binary(IrBinaryOp.Subtract, to, from));
      var more = this.Fresh("more", IrType.Bool);
      setup.Add(new IrDeclare(more, Ascending(Binary(IrBinaryOp.Less, Ref(counter), stop), Binary(IrBinaryOp.Greater, Ref(counter), stop))));
      var next = Ascending(
        Binary(IrBinaryOp.Greater, Distance(Ref(counter), stop), Convert(IrType.U64, step)),
        Binary(IrBinaryOp.Greater, Distance(stop, Ref(counter)), Convert(IrType.U64, Binary(IrBinaryOp.Subtract, Int(0), step))));
      return [.. setup, new IrFor(Ref(more), new(lowered), Block(Assign(more, next), Assign(counter, Binary(IrBinaryOp.Add, Ref(counter), step))))];
    });
  }

  /// <summary>The names a list of statements assigns, also in nested blocks.</summary>
  private static HashSet<string> Assigned(IReadOnlyList<PyNode> statements) {
    var names = new HashSet<string>();
    void Visit(PyNode statement) {
      foreach (var target in statement.Kind switch {
        "Assign" => statement.Nodes("targets").SelectMany(ScopeResolver.Targets),
        "AugAssign" or "AnnAssign" or "For" => ScopeResolver.Targets(statement.Node("target")!),
        "Match" => statement.Nodes("cases").SelectMany(matchCase => ScopeResolver.Captures(matchCase.Node("pattern")!)),
        _ => []
      })
        names.Add(target.Text("id") ?? target.Text("name")!);

      foreach (var inner in statement.Nodes("body").Concat(statement.Nodes("orelse")).Concat(statement.Nodes("cases").SelectMany(matchCase => matchCase.Nodes("body"))))
        Visit(inner);
    }

    foreach (var statement in statements)
      Visit(statement);

    return names;
  }

  /// <summary>Whether a name occurs anywhere in a function outside one loop, its target aside.</summary>
  private static bool UsedOutside(IReadOnlyList<PyNode> statements, PyNode loop, string name) {
    bool Uses(PyNode node) {
      if (ReferenceEquals(node, loop))
        return false;

      if ((node.Is("Name") && node.Text("id") == name) || (node.Is("MatchAs") && node.Text("name") == name))
        return true;

      foreach (var field in new[] { "value", "test", "left", "right", "operand", "func", "msg", "target", "iter", "subject", "pattern", "guard" })
        if (node.Node(field) is { } inner && Uses(inner))
          return true;

      foreach (var field in new[] { "body", "orelse", "values", "comparators", "args", "elts", "targets", "cases", "patterns" })
        foreach (var inner in node.Nodes(field))
          if (Uses(inner))
            return true;

      return false;
    }

    return statements.Any(Uses);
  }

  /// <summary>
  /// <c>match</c>: the subject evaluated once, the cases tried in source order. A literal is compared
  /// with <c>==</c>, so <c>case 1:</c> matches <c>1.0</c> and <c>True</c> too; <c>True</c>,
  /// <c>False</c> and <c>None</c> are compared by identity and match only themselves; a capture
  /// binds the subject before its guard is tested, and a false guard goes on with the next case.
  /// </summary>
  private IEnumerable<IrStmt> Match(PyNode statement) {
    var subject = this.Value(statement.Node("subject")!);
    var result = new List<IrStmt>();
    if (subject.Value is not (IrConst or IrLocalRef)) {
      var temporary = this.Fresh("subject", subject.Value.Type);
      result.Add(new IrDeclare(temporary, subject.Value));
      subject = (Ref(temporary), subject.Type);
    }

    result.AddRange(this.Cases(statement.Nodes("cases"), 0, subject));
    return result;
  }

  private List<IrStmt> Cases(IReadOnlyList<PyNode> cases, int index, (IrExpr Value, PyType Type) subject) {
    if (index == cases.Count)
      return [];

    var matchCase = cases[index];
    var pattern = matchCase.Node("pattern")!;
    var guard = matchCase.Node("guard");
    if (pattern.Is("MatchAs")) {
      var bound = new List<IrStmt>();
      if (pattern.Text("name") is { } name)
        bound.Add(Assign(this._variables[name], this.Coerce(subject, this._function.Find(name)!.Type)));

      if (guard is null)
        return [.. bound, .. this.Statements(matchCase.Nodes("body"))];

      var test = this.Guard(guard);
      var rest = this.Cases(cases, index + 1, subject);
      return [.. bound, new IrIf(test, new(this.Statements(matchCase.Nodes("body"))), rest.Count == 0 ? null : new(rest))];
    }

    var tests = this.Alternatives(pattern, subject).ToList();
    if (tests.Count == 0)
      return this.Cases(cases, index + 1, subject);

    var condition = tests.Aggregate((first, second) => Binary(IrBinaryOp.LogicalOr, first, second));
    if (guard is not null)
      condition = Binary(IrBinaryOp.LogicalAnd, condition, this.Guard(guard));

    var then = this.Statements(matchCase.Nodes("body"));
    var otherwise = this.Cases(cases, index + 1, subject);
    return [new IrIf(condition, new(then), otherwise.Count == 0 ? null : new(otherwise))];
  }

  /// <summary>The tests of the alternatives of a literal pattern; none for a pattern that never matches the subject's type.</summary>
  private IEnumerable<IrExpr> Alternatives(PyNode pattern, (IrExpr Value, PyType Type) subject) {
    switch (pattern.Kind) {
      case "MatchOr":
        foreach (var alternative in pattern.Nodes("patterns"))
          foreach (var test in this.Alternatives(alternative, subject))
            yield return test;

        break;
      case "MatchSingleton":
        // True and False are compared by identity: they match a bool only; None matches no number.
        if (pattern.Text("kind") == "bool" && subject.Type == PyType.Bool)
          yield return pattern.Text("value") == "True" ? subject.Value : Not(subject.Value);

        break;
      default:
        yield return this.Comparison("Eq", subject, this.Value(pattern.Node("value")!));
        break;
    }
  }

  /// <summary>The truth of a guard, which is evaluated only after its pattern matched: it must not need a temporary.</summary>
  private IrExpr Guard(PyNode guard) {
    ++this._conditional;
    var test = this.Truth(this.Value(guard));
    --this._conditional;
    return test;
  }

}
