using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;
using Polypiler.Core.Frontends.Python.Typing;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

internal sealed partial class FunctionLowering {

  /// <summary>
  /// <c>for</c>: over <c>range(…)</c> a counted loop; over a list, a tuple, bytes or a table an index
  /// loop that reads the length again before every pass, so that a list that grows in the body is
  /// iterated further, as CPython's list iterator does; over a str its code points; over
  /// <c>enumerate</c> and <c>zip</c> the same loop with a second counter or a second sequence; over
  /// <c>reversed</c> an index loop down from the end; over a generator or an enum class its values.
  /// </summary>
  private IEnumerable<IrStmt> For(PyNode statement) {
    var iterable = statement.Node("iter")!;
    var body = statement.Nodes("body");
    if (this.BuiltinCall(iterable) == "range")
      return this.WithElse(statement, () => this.Range(statement.Node("target")!, iterable, () => this.Statements(body), statement));

    return this.WithElse(statement, () => this.Loop(statement.Node("target")!, iterable, () => this.Statements(body)));
  }

  /// <summary>A loop of a target over an iterable whose passes run the statements a body makes; the statements of the loop.</summary>
  private List<IrStmt> Loop(PyNode target, PyNode iterable, System.Func<List<IrStmt>> body) {
    if (this.BuiltinCall(iterable) is { } builtin) {
      switch (builtin) {
        case "range":
          return this.Range(target, iterable, body, null);
        case "enumerate":
          return this.Enumerate(target, iterable, body);
        case "zip":
          return this.Zip(target, iterable, body);
        case "reversed":
          return this.Reversed(target, iterable, body);
      }
    }

    if (iterable.Is("Name") && this.Scope.Resolve(iterable) == Binding.Class && this.Scope.Class(iterable) is { IsEnum: true } enumeration)
      return this.Indexed(target, this.MemberTable(enumeration), body);

    var sequence = this.Value(iterable);
    return sequence.Type.Kind switch {
      PyKind.Iterator => this.Each(target, sequence, body),
      PyKind.Str => this.CodePoints(target, sequence, body),
      PyKind.List or PyKind.Tuple or PyKind.Bytes or PyKind.ByteArray or PyKind.Table => this.Indexed(target, sequence, body),
      _ => throw this.Refuse(iterable, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{this.Quote(iterable)}' of the type {sequence.Type} is not supported")
    };
  }

  /// <summary>The name of the built-in a call calls, or <see langword="null"/> for any other expression.</summary>
  private string? BuiltinCall(PyNode node)
    => node.Is("Call") && node.Node("func") is { Kind: "Name" } callee && this.Scope.Resolve(callee) == Binding.BuiltIn ? callee.Text("id") : null;

  /// <summary>The statements that bind a loop target to a value: a name, or the names of a tuple from its items.</summary>
  private List<IrStmt> BindTarget(PyNode target, (IrExpr Value, PyType Type) value) {
    if (!(target.Is("Tuple") || target.Is("List")))
      return this.Store(target, value);

    var names = target.Nodes("elts");
    if (value.Type.Kind != PyKind.Tuple || value.Type.Items.Count != names.Count)
      throw this.Unsupported(target, $"unpacking a value of the type {value.Type} into {names.Count} names");

    var result = new List<IrStmt>();
    var held = this.Once(value, result);
    var fields = PyTypes.Ir(value.Type).StructType!.Fields;
    for (var index = 0; index < names.Count; ++index)
      result.AddRange(this.Store(names[index], (new IrFieldLoad(held.Value, fields[index]), value.Type.Items[index])));

    return result;
  }

  /// <summary>An index loop: a counter from 0 while it is below the length the test reads anew, the element read at the start of every pass.</summary>
  private List<IrStmt> Indexed(PyNode target, (IrExpr Value, PyType Type) sequence, System.Func<List<IrStmt>> body) {
    var setup = new List<IrStmt>();
    var held = this.Once(sequence, setup);
    var counter = this.Fresh("i", IrType.I32);
    setup.Add(new IrDeclare(counter, Int32(0)));
    var pass = this.BindTarget(target, this.ElementAt(held, Ref(counter)));
    pass.AddRange(body());
    return [.. setup, new IrFor(Binary(IrBinaryOp.Less, Ref(counter), this.Length32(held)), new(pass), Block(Assign(counter, Binary(IrBinaryOp.Add, Ref(counter), Int32(1)))))];
  }

  /// <summary><c>for i, x in enumerate(a, start)</c>: the index loop with a second counter from <c>start</c>, which is evaluated once, before the loop.</summary>
  private List<IrStmt> Enumerate(PyNode target, PyNode call, System.Func<List<IrStmt>> body) {
    var arguments = call.Nodes("args");
    if (!target.Is("Tuple") || target.Nodes("elts").Count != 2 || arguments.Count is < 1 or > 2 || call.Nodes("keywords").Count > 0)
      throw this.Refuse(call, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{this.Quote(call)}' is not supported: enumerate takes one sequence and a start, its target two names");

    var setup = new List<IrStmt>();
    var sequence = this.Once(this.Value(arguments[0]), setup);
    if (!sequence.Type.IsSequence || sequence.Type.Kind == PyKind.Str)
      throw this.Refuse(call, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{this.Quote(call)}' is not supported: enumerate of the type {sequence.Type}");

    var start = arguments.Count == 2 ? this.Once((this.Integer(this.Plain(this.Value(arguments[1]), arguments[1])).Item1, PyType.Int), setup).Value : Big(0);
    var counter = this.Fresh("i", IrType.I32);
    setup.Add(new IrDeclare(counter, Int32(0)));
    var names = target.Nodes("elts");
    var pass = new List<IrStmt>();
    // The number of a pass is the start plus the index, exact.
    pass.AddRange(this.Store(names[0], (Binary(IrBinaryOp.Add, start, Exact(Ref(counter))), PyType.Int)));
    pass.AddRange(this.Store(names[1], this.ElementAt(sequence, Ref(counter))));
    pass.AddRange(body());
    return [.. setup, new IrFor(Binary(IrBinaryOp.Less, Ref(counter), this.Length32(sequence)), new(pass), Block(Assign(counter, Binary(IrBinaryOp.Add, Ref(counter), Int32(1)))))];
  }

  /// <summary><c>for x, y in zip(a, b)</c>: one counter; the loop ends where either sequence ends, both lengths read anew before every pass.</summary>
  private List<IrStmt> Zip(PyNode target, PyNode call, System.Func<List<IrStmt>> body) {
    var arguments = call.Nodes("args");
    if (!target.Is("Tuple") || target.Nodes("elts").Count != 2 || arguments.Count != 2 || call.Nodes("keywords").Count > 0)
      throw this.Refuse(call, PythonFrontend.UnsupportedSequenceDescriptor, "sequence operation 'zip of other than two sequences into two names' is not supported by the Python frontend");

    var setup = new List<IrStmt>();
    var first = this.Once(this.Value(arguments[0]), setup);
    var second = this.Once(this.Value(arguments[1]), setup);
    foreach (var (sequence, argument) in new[] { (first, arguments[0]), (second, arguments[1]) })
      if (!sequence.Type.IsSequence || sequence.Type.Kind == PyKind.Str)
        throw this.Refuse(argument, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{this.Quote(call)}' is not supported: zip of the type {sequence.Type}");

    var counter = this.Fresh("i", IrType.I32);
    setup.Add(new IrDeclare(counter, Int32(0)));
    var names = target.Nodes("elts");
    var pass = new List<IrStmt>();
    var x = this.Once(this.ElementAt(first, Ref(counter)), pass);
    var y = this.Once(this.ElementAt(second, Ref(counter)), pass);
    pass.AddRange(this.Store(names[0], x));
    pass.AddRange(this.Store(names[1], y));
    pass.AddRange(body());
    var test = Binary(IrBinaryOp.LogicalAnd, Binary(IrBinaryOp.Less, Ref(counter), this.Length32(first)), Binary(IrBinaryOp.Less, Ref(counter), this.Length32(second)));
    return [.. setup, new IrFor(test, new(pass), Block(Assign(counter, Binary(IrBinaryOp.Add, Ref(counter), Int32(1)))))];
  }

  /// <summary>
  /// <c>for x in reversed(a)</c>: an index from the last element down, which goes on while it lies
  /// within the sequence as it is now - CPython's reverse iterator stops where the list shrank below it.
  /// </summary>
  private List<IrStmt> Reversed(PyNode target, PyNode call, System.Func<List<IrStmt>> body) {
    var arguments = call.Nodes("args");
    if (arguments.Count != 1 || call.Nodes("keywords").Count > 0)
      throw this.Refuse(call, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{this.Quote(call)}' is not supported");

    var setup = new List<IrStmt>();
    var sequence = this.Once(this.Value(arguments[0]), setup);
    if (!sequence.Type.IsSequence || sequence.Type.Kind == PyKind.Str)
      throw this.Refuse(call, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{this.Quote(call)}' is not supported: reversed of the type {sequence.Type}");

    var counter = this.Fresh("i", IrType.I32);
    setup.Add(new IrDeclare(counter, Binary(IrBinaryOp.Subtract, this.Length32(sequence), Int32(1))));
    var pass = this.BindTarget(target, this.ElementAt(sequence, Ref(counter)));
    pass.AddRange(body());
    var test = Binary(IrBinaryOp.LogicalAnd, Binary(IrBinaryOp.GreaterOrEqual, Ref(counter), Int32(0)), Binary(IrBinaryOp.Less, Ref(counter), this.Length32(sequence)));
    return [.. setup, new IrFor(test, new(pass), Block(Assign(counter, Binary(IrBinaryOp.Subtract, Ref(counter), Int32(1)))))];
  }

  /// <summary><c>for c in s</c> of a str: its code points, a pair of surrogates as one, each a str of its own.</summary>
  private List<IrStmt> CodePoints(PyNode target, (IrExpr Value, PyType Type) text, System.Func<List<IrStmt>> body) {
    var setup = new List<IrStmt>();
    var held = this.Once(text, setup);
    var at = this.Fresh("at", IrType.I32);
    var width = this.Fresh("width", IrType.I32);
    setup.Add(new IrDeclare(at, Int32(0)));
    setup.Add(new IrDeclare(width, Int32(0)));
    var pass = new List<IrStmt> { Assign(width, Intrinsic(IrIntrinsicOp.StrNextCodePoint, IrType.I32, held.Value, Ref(at))) };
    pass.AddRange(this.Store(target, (Intrinsic(IrIntrinsicOp.StrSubstring, IrType.String, held.Value, Ref(at), Ref(width)), PyType.Str)));
    pass.AddRange(body());
    var length = Intrinsic(IrIntrinsicOp.StrLength, IrType.I32, held.Value);
    return [.. setup, new IrFor(Binary(IrBinaryOp.Less, Ref(at), length), new(pass), Block(Assign(at, Binary(IrBinaryOp.Add, Ref(at), Ref(width)))))];
  }

  /// <summary>The members of an enum as a table of the module, in definition order.</summary>
  private (IrExpr Value, PyType Type) MemberTable(ClassModel enumeration) {
    var type = PyType.Enum(enumeration);
    var name = enumeration.Name + "Members";
    var members = this._context.Tables.FirstOrDefault(table => table.Name == name);
    if (members is null) {
      var element = PyTypes.Ir(type);
      members = new IrGlobal(name, IrType.Array(element), Intrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(element), [.. enumeration.Members.Select(member => (IrExpr)new IrConst(element, member.Value))]));
      this._context.Tables.Add(members);
    }

    return (new IrGlobalRef(members), PyType.Table(type));
  }

  /// <summary><c>for x in g</c> of a generator, of <c>map</c> or <c>filter</c>: the loop over the sequence, which is closed however the loop ends.</summary>
  private List<IrStmt> Each(PyNode target, (IrExpr Value, PyType Type) sequence, System.Func<List<IrStmt>> body) {
    var element = sequence.Type.Element!;
    var value = this.Fresh("item", PyTypes.Ir(element));
    var pass = this.BindTarget(target, (Ref(value), element));
    pass.AddRange(body());
    return [new IrForEachSequence(value, sequence.Value, new(pass))];
  }

  /// <summary>
  /// <c>for v in range(…)</c>: the arguments evaluated once, in order, before the loop; a zero step
  /// raises <c>ValueError</c> before the first pass. A counter walks the range and the loop variable
  /// is assigned from it at the start of every pass, so that assigning the variable in the body skips
  /// nothing; where the body never assigns it and nothing reads it after the loop, the variable is
  /// the counter. The counter is an <c>int</c> as exact as the bounds, so no element is lost or added
  /// at any magnitude; a step of 1 or -1 tests the bound directly, any other step tests it by its sign.
  /// </summary>
  private List<IrStmt> Range(PyNode targetNode, PyNode range, System.Func<List<IrStmt>> bodyOf, PyNode? statement) {
    var body = statement?.Nodes("body") ?? [];
    if (!targetNode.Is("Name") || range.Nodes("args").Count is < 1 or > 3 || range.Nodes("keywords").Count > 0)
      throw this.Refuse(range, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{this.Quote(range)}' is not supported");

    var target = targetNode.Text("id")!;
    var assigned = Assigned(body);
    var setup = new List<IrStmt>();
    var arguments = new List<IrExpr>();
    foreach (var argument in range.Nodes("args")) {
      var value = this.Value(argument);
      if (!value.Type.IsNumber || value.Type.Kind == PyKind.Float)
        throw this.Refuse(argument, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{this.Quote(range)}' is not supported: a {value.Type} argument raises TypeError");

      var integer = this.Integer(value).Item1;
      if (!(integer is IrConst or IrBigConst || (integer is IrLocalRef local && !assigned.Contains(local.Local.Name) && local.Local.Name != target))) {
        var temporary = this.Fresh(arguments.Count == 0 && range.Nodes("args").Count > 1 ? "start" : arguments.Count == 2 ? "step" : "stop", IrType.BigInt);
        setup.Add(new IrDeclare(temporary, integer));
        integer = Ref(temporary);
      }

      arguments.Add(integer);
    }

    var (start, stop, step) = arguments.Count switch {
      1 => (Big(0), arguments[0], (IrExpr)Big(1)),
      2 => (arguments[0], arguments[1], Big(1)),
      _ => (arguments[0], arguments[1], arguments[2])
    };
    if (step is IrBigConst { Value.IsZero: true })
      return [.. setup, Raise(IrExceptionClass.ArgumentException, "range() arg 3 must not be zero")];

    var variable = this.Scope.Variable(targetNode)!;
    var loopLocal = this.Local(variable);
    // The variable is the counter where nothing but the loop reads or assigns it: a statement's own local, used nowhere after it.
    var direct = statement is not null && this.Function.Locals.ContainsValue(variable) && variable.Type.Kind == PyKind.Int && !assigned.Contains(target)
      && !UsedOutside(this.Function.Body, statement, target) && !this.Function.Nested.Values.Any(nested => nested.Find(target) == variable);
    var counter = direct ? loopLocal : this.Fresh("k", IrType.BigInt);
    if (!direct)
      setup.Add(new IrDeclare(counter, Big(0)));

    if (step is not IrBigConst)
      setup.Add(If(Equal(step, Big(0)), Raise(IrExceptionClass.ArgumentException, "range() arg 3 must not be zero")));

    setup.Add(Assign(counter, start));
    var lowered = new List<IrStmt>();
    if (!direct)
      lowered.AddRange(this.Store(targetNode, (Ref(counter), PyType.Int)));

    lowered.AddRange(bodyOf());
    {

      if (step is IrBigConst unit && BigInteger.Abs(unit.Value).IsOne) {
        var up = unit.Value.Sign > 0;
        var loop = new IrFor(Binary(up ? IrBinaryOp.Less : IrBinaryOp.Greater, Ref(counter), stop), new(lowered),
          Block(Assign(counter, Binary(up ? IrBinaryOp.Add : IrBinaryOp.Subtract, Ref(counter), Big(1)))));
        return [.. setup, loop];
      }

      IrExpr Ascending(IrExpr positive, IrExpr negative) => step is IrBigConst constant ? constant.Value.Sign > 0 ? positive : negative : Choose(Binary(IrBinaryOp.Greater, step, Big(0)), positive, negative);
      IrExpr Inside() => Ascending(Binary(IrBinaryOp.Less, Ref(counter), stop), Binary(IrBinaryOp.Greater, Ref(counter), stop));
      var more = this.Fresh("more", IrType.Bool);
      setup.Add(new IrDeclare(more, Inside()));
      return [.. setup, new IrFor(Ref(more), new(lowered), Block(Assign(counter, Binary(IrBinaryOp.Add, Ref(counter), step)), Assign(more, Inside())))];
    }
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

      foreach (var expression in ScopeResolver.Expressions(statement))
        foreach (var node in ScopeResolver.Walk(expression).Where(node => node.Is("NamedExpr")))
          names.Add(node.Node("target")!.Text("id")!);

      foreach (var inner in statement.Nodes("body").Concat(statement.Nodes("orelse")).Concat(statement.Nodes("finalbody"))
        .Concat(statement.Nodes("handlers").SelectMany(handler => handler.Nodes("body")))
        .Concat(statement.Nodes("cases").SelectMany(matchCase => matchCase.Nodes("body"))))
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

      foreach (var field in new[] { "value", "test", "left", "right", "operand", "func", "msg", "target", "iter", "subject", "pattern", "guard", "slice", "elt", "exc" })
        if (node.Node(field) is { } inner && Uses(inner))
          return true;

      foreach (var field in new[] { "body", "orelse", "finalbody", "handlers", "values", "comparators", "args", "elts", "targets", "cases", "patterns", "keywords", "generators", "ifs" })
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
  /// <c>False</c> and <c>None</c> are compared by identity and match only themselves; a member of an
  /// enum matches that member; a capture binds the subject before its guard is tested, and a false
  /// guard goes on with the next case.
  /// </summary>
  private IEnumerable<IrStmt> Match(PyNode statement) {
    var subject = this.Value(statement.Node("subject")!);
    var result = new List<IrStmt>();
    if (subject.Value is not (IrConst or IrBigConst or IrLocalRef)) {
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
        bound.Add(Assign(this.Local(this.Function.Find(name)!), this.Coerce(subject, this.Function.Find(name)!.Type, pattern)));

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
        // True and False are compared by identity: they match a bool only; None matches an optional that holds none.
        if (pattern.Text("kind") == "bool" && subject.Type.Kind == PyKind.Bool)
          yield return pattern.Text("value") == "True" ? subject.Value : Not(subject.Value);
        else if (pattern.Text("kind") == "none" && subject.Type.Kind == PyKind.Optional)
          yield return this.IsNone(subject);

        break;
      default:
        var value = this.Lower(pattern.Node("value")!);
        if (value.Type.Kind == PyKind.Str != (subject.Type.Kind == PyKind.Str))
          break;

        yield return this.Comparison(pattern, "Eq", subject, value);
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
