using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Kotlin.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Kotlin.Lowering;

internal sealed partial class KotlinLowerer {

  #region Loops

  /// <summary>
  /// Lowers a loop inside a frame of its own. A labelled jump from an inner loop to this one sets a
  /// flag and leaves the inner loop; after every inner loop the flags of the loops around it are
  /// tested, which leaves or continues the target - the IR has no labelled jump. A flag is cleared
  /// before its loop starts again.
  /// </summary>
  private void Looped(string? label, List<IrStmt> into, Func<IrStmt> build) {
    var frame = new Loop(label);
    this._loops.Add(frame);
    IrStmt loop;
    try {
      loop = build();
    } finally {
      this._loops.RemoveAt(this._loops.Count - 1);
    }

    if (frame.BreakFlag is { } broke)
      into.Add(new IrAssign(broke, new IrConst(IrType.Bool, 0)));

    into.Add(loop);
    // The jumps of an inner loop to a loop further out go on from here.
    for (var index = 0; index < this._loops.Count; ++index) {
      var outer = this._loops[index];
      var direct = index == this._loops.Count - 1;
      if (outer.BreakFlag is { } flag)
        into.Add(new IrIf(new IrLocalRef(flag), new IrBlock([new IrBreak()]), null));

      if (outer.ContinueFlag is { } next)
        into.Add(new IrIf(new IrLocalRef(next), new IrBlock(direct ? [new IrAssign(next, new IrConst(IrType.Bool, 0)), new IrContinue()] : [new IrBreak()]), null));
    }
  }

  private IrLocal Flag(string name) {
    var flag = this.NewLocal(name, IrType.Bool);
    this._flags.Add(flag);
    return flag;
  }

  private void Jump(KStatement jump, List<IrStmt> into) {
    var (label, isBreak) = jump switch {
      KBreak broke => (broke.Label, true),
      _ => (((KContinue)jump).Label, false)
    };
    if (this._loops.Count == 0)
      throw Refuse(KotlinFrontend.Control, jump, $"'{(isBreak ? "break" : "continue")}' outside every loop");

    var index = label is null ? this._loops.Count - 1 : this._loops.FindLastIndex(loop => loop.Label == label);
    if (index < 0)
      throw Refuse(KotlinFrontend.Control, jump, $"there is no loop labelled '{label}'");

    if (index == this._loops.Count - 1) {
      into.Add(isBreak ? new IrBreak() : new IrContinue());
      return;
    }

    var target = this._loops[index];
    var flag = isBreak ? target.BreakFlag ??= this.Flag($"break{label}") : target.ContinueFlag ??= this.Flag($"continue{label}");
    into.Add(new IrAssign(flag, new IrConst(IrType.Bool, 1)));
    into.Add(new IrBreak());
  }

  /// <summary><c>while</c>: a condition that needs statements is tested at the top of an endless loop, so that every pass evaluates it anew.</summary>
  private void While(KWhile loop, List<IrStmt> into) => this.Looped(loop.Label, into, () => {
    var condition = this.Condition(loop.Condition);
    if (this._pending.Count == 0)
      return new IrWhile(condition, this.Body(loop.Body));

    var statements = new List<IrStmt>();
    this.Flush(statements);
    statements.Add(new IrIf(new IrUnary(IrUnaryOp.LogicalNot, condition), new IrBlock([new IrBreak()]), null));
    statements.AddRange(this.Body(loop.Body).Statements);
    return new IrWhile(new IrConst(IrType.Bool, 1), new IrBlock(statements));
  });

  /// <summary><c>do</c>-<c>while</c>: the condition sees the variables of the body, as Kotlin's does.</summary>
  private void DoWhile(KDoWhile loop, List<IrStmt> into) => this.Looped(loop.Label, into, () => this.Scoped(() => {
    var statements = new List<IrStmt>();
    if (loop.Body is KBlock block)
      foreach (var statement in block.Statements)
        this.Statement(statement, statements);
    else
      this.Statement((KStatement)loop.Body, statements);

    var condition = this.Condition(loop.Condition);
    if (this._pending.Count > 0)
      throw Refuse(KotlinFrontend.Control, loop.Condition, "the condition of a do-while loop that needs statements is outside the imported subset");

    return (IrStmt)new IrDoWhile(new IrBlock(statements), condition);
  }));

  /// <summary>
  /// <c>for</c> over a range, a progression, an array or a string: an IR <c>for</c> over a counter
  /// that never steps past the last element, so that a range ending at the largest value ends; the
  /// bounds and the step are evaluated once, before the loop, the loop variable is a <c>val</c> of
  /// every pass.
  /// </summary>
  private void For(KFor loop, List<IrStmt> into) {
    if (loop.Range is KCall { Callee: KMember { Name: "withIndex" } })
      throw Refuse(KotlinFrontend.Control, loop, "a for loop over withIndex is outside the imported subset");

    if (this.RangeOf(loop.Range) is { } range) {
      this.Counted(loop, range, into);
      return;
    }

    var sequence = this.Expression(loop.Range, null);
    if (!sequence.Type.IsArray && !sequence.Type.IsString)
      throw Refuse(KotlinFrontend.Control, loop.Range, $"a for loop over a {sequence.Type} is outside the imported subset");

    var source = this.Once(sequence, sequence.Type.IsString ? "text" : "array");
    this.Flush(into);
    var index = this.NewLocal("index", IrType.I32);
    into.Add(new IrDeclare(index, new IrConst(IrType.I32, 0)));
    IrExpr length = source.Type.IsString ? new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [source]) : new IrArrayLength(source);
    IrExpr element = source.Type.IsString
      ? new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [source, new IrLocalRef(index)])
      : new IrArrayLoad(source, new IrLocalRef(index));
    this.Looped(loop.Label, into, () => this.Scoped(() => {
      var variable = this.NewLocal(loop.Variable, element.Type);
      this.Scope.Add(loop.Variable, new Variable(variable, false));
      if (source.Type.IsString)
        this._charLocals.Add(variable);

      var body = new List<IrStmt> { new IrDeclare(variable, element) };
      body.AddRange(this.Body(loop.Body).Statements);
      return (IrStmt)new IrFor(new IrBinary(IrBinaryOp.Less, new IrLocalRef(index), length), new IrBlock(body),
        new IrBlock([new IrAssign(index, new IrBinary(IrBinaryOp.Add, new IrLocalRef(index), new IrConst(IrType.I32, 1)))]));
    }));
  }

  /// <summary>A range of a <c>for</c> loop: its first and last element or end, its step and direction, the end inclusive or not.</summary>
  private sealed record CountedRange(KExpression First, KExpression End, bool Inclusive, bool Down, KExpression? Step, KNode At);

  /// <summary>The range a <c>for</c> loop runs over, where it is one: <c>a..b</c>, <c>a..&lt;b</c>, <c>a until b</c>, <c>a downTo b</c>, each with a <c>step</c>, and <c>array.indices</c>.</summary>
  private CountedRange? RangeOf(KExpression range) {
    switch (range) {
      case KBinary { Operator: "step" } stepped when this.RangeOf(stepped.Left) is { Step: null } inner:
        return inner with { Step = stepped.Right };
      case KBinary { Operator: ".." } binary:
        return new(binary.Left, binary.Right, true, false, null, binary);
      case KBinary { Operator: "..<" or "until" } binary:
        return new(binary.Left, binary.Right, false, false, null, binary);
      case KBinary { Operator: "downTo" } binary:
        return new(binary.Left, binary.Right, true, true, null, binary);
      case KMember { Name: "indices", Receiver: var receiver } member:
        return new(new KInteger(0, false, false) { At = member.At }, new KMember(receiver, "size", false) { At = member.At }, false, false, null, member);
      default:
        return null;
    }
  }

  private void Counted(KFor loop, CountedRange range, List<IrStmt> into) {
    var first = this.Expression(range.First, null);
    var end = this.Expression(range.End, null);
    var type = this.IsChar(first) && this.IsChar(end) ? IrType.U16 : Arithmetic(first.Type, end.Type)
      ?? throw Refuse(KotlinFrontend.Control, range.At, $"a range from a {first.Type} to a {end.Type} is outside the imported subset");
    if (type.IsFloat)
      throw Refuse(KotlinFrontend.Control, range.At, "a for loop over a range of floating-point numbers is outside the imported subset: Kotlin has no such progression");

    // A range of characters counts their codes in Int, which the IR compares.
    var isChar = type == IrType.U16;
    var element = type;
    if (isChar) {
      (first, end, type) = (new IrConvert(IrType.I32, first), new IrConvert(IrType.I32, end), IrType.I32);
    }

    var start = this.Once(this.Coerce(first, type, range.First), "first");
    var last = this.Once(this.Coerce(end, type, range.End), range.Inclusive ? "last" : "end");
    IrExpr? step = null;
    if (range.Step is { } stepExpression) {
      var stepType = type.IsSigned ? (type == IrType.I64 ? IrType.I64 : IrType.I32) : type == IrType.U64 ? IrType.I64 : IrType.I32;
      step = this.Once(this.Coerce(this.Expression(stepExpression, stepType), stepType, stepExpression), "step");
      this.Flush(into);
      // Kotlin's step must be positive, or it throws an IllegalArgumentException before the loop.
      into.Add(new IrIf(new IrBinary(IrBinaryOp.LessOrEqual, step, new IrConst(step.Type, 0)),
        new IrBlock([new IrThrow(this.NewException(IrExceptionClass.ArgumentException, "Step must be positive."))]), null));
    }

    this.Flush(into);
    var counter = this.NewLocal("counter", type);
    var go = this.NewLocal("more", IrType.Bool);
    IrExpr Counter() => new IrLocalRef(counter);
    var (before, after) = range.Down ? (IrBinaryOp.GreaterOrEqual, IrBinaryOp.Subtract) : (IrBinaryOp.LessOrEqual, IrBinaryOp.Add);
    into.Add(new IrDeclare(counter, start));
    into.Add(new IrDeclare(go, new IrBinary(range.Inclusive ? before : range.Down ? IrBinaryOp.Greater : IrBinaryOp.Less, start, last)));
    var unsigned = type == IrType.I64 || type == IrType.U64 ? IrType.U64 : IrType.U32;
    IrExpr Distance() => new IrConvert(unsigned, range.Down ? new IrBinary(IrBinaryOp.Subtract, Counter(), last) : new IrBinary(IrBinaryOp.Subtract, last, Counter()));
    IrExpr amount = step is null ? new IrConst(type, 1) : this.Coerce(step, type, range.At);
    // There is a next element while the distance to the last one is at least the step; it is measured
    // without the sign, as the counter never passes the last element.
    IrExpr more = range.Inclusive && step is null ? new IrBinary(IrBinaryOp.NotEqual, Counter(), last)
      : range.Inclusive ? new IrBinary(IrBinaryOp.GreaterOrEqual, Distance(), new IrConvert(unsigned, step!))
      : step is null ? new IrBinary(IrBinaryOp.Less, new IrBinary(after, Counter(), amount), last)
      : new IrBinary(IrBinaryOp.Greater, Distance(), new IrConvert(unsigned, step));
    var advance = new IrAssign(counter, new IrBinary(after, Counter(), amount));
    // An exclusive end compares the stepped counter, so the counter steps first.
    IrStmt[] steps = range.Inclusive || step is not null ? [new IrAssign(go, more), advance] : [advance, new IrAssign(go, new IrBinary(IrBinaryOp.Less, Counter(), last))];
    this.Looped(loop.Label, into, () => this.Scoped(() => {
      var variable = this.NewLocal(loop.Variable, element);
      this.Scope.Add(loop.Variable, new Variable(variable, false));
      if (isChar)
        this._charLocals.Add(variable);

      var body = new List<IrStmt> { new IrDeclare(variable, isChar ? new IrConvert(IrType.U16, Counter()) : Counter()) };
      body.AddRange(this.Body(loop.Body).Statements);
      return (IrStmt)new IrFor(new IrLocalRef(go), new IrBlock(body), new IrBlock(steps));
    }));
  }

  #endregion

  #region when

  /// <summary>
  /// <c>when</c>: with a subject whose every condition is a constant integer and no branch of which
  /// jumps, an IR switch; otherwise a chain of <c>if</c>s tested in order, as Kotlin tests its branches.
  /// A condition that needs statements is evaluated only once the conditions before it failed.
  /// </summary>
  private void When(KWhen choice, List<IrStmt> into, Action<KNode, List<IrStmt>> branch, bool value) {
    IrExpr? subject = null;
    if (choice.Subject is { } expression) {
      subject = this.Once(this.Expression(expression, null), "subject");
      this.Flush(into);
    }

    var defaults = choice.Entries.Where(entry => entry.Conditions.Count == 0).ToList();
    if (value && defaults.Count == 0)
      throw Refuse(KotlinFrontend.Control, choice, "a when whose value is used needs an else branch in the imported subset");

    if (subject is not null && subject.Type.IsInteger && !choice.Entries.Any(entry => Jumps(entry.Body)) && this.Switch(choice, subject, branch) is { } selection) {
      into.Add(selection);
      return;
    }

    into.AddRange(this.Chain(choice.Entries, 0, subject, branch));
  }

  /// <summary>Whether a branch has a <c>break</c> or a <c>continue</c>, which an IR switch would take as its own.</summary>
  private static bool Jumps(KNode node) => node switch {
    KBreak or KContinue => true,
    KJumpExpression { Jump: KBreak or KContinue } => true,
    KBlock block => block.Statements.Any(Jumps),
    KExpressionStatement statement => Jumps(statement.Expression),
    KIf conditional => Jumps(conditional.Then) || conditional.Else is { } otherwise && Jumps(otherwise),
    KWhen choice => choice.Entries.Any(entry => Jumps(entry.Body)),
    KWhile or KDoWhile or KFor => false,
    _ => false
  };

  /// <summary>The switch of a <c>when</c> whose every condition is a distinct constant of the subject's type; <see langword="null"/> otherwise.</summary>
  private IrSwitch? Switch(KWhen choice, IrExpr subject, Action<KNode, List<IrStmt>> branch) {
    var labels = new List<List<long>>();
    var seen = new HashSet<long>();
    foreach (var entry in choice.Entries) {
      var values = new List<long>();
      foreach (var condition in entry.Conditions) {
        if (condition is KWhenRange || this.Probe(() => this.Expression(condition, subject.Type)) is not IrConst constant || !seen.Add(this.Coerce(constant, subject.Type, condition) is IrConst typed ? typed.Value : long.MinValue))
          return null;

        values.Add(((IrConst)this.Coerce(constant, subject.Type, condition)).Value);
      }

      labels.Add(values);
    }

    var sections = new List<IrSwitchSection>();
    for (var index = 0; index < choice.Entries.Count; ++index) {
      var body = new List<IrStmt>();
      branch(choice.Entries[index].Body, body);
      sections.Add(new IrSwitchSection(labels[index], choice.Entries[index].Conditions.Count == 0, new IrBlock(body)));
    }

    return new IrSwitch(subject, sections);
  }

  /// <summary>The branches from one on as <c>if</c>/<c>else</c>: the first whose condition holds runs.</summary>
  private List<IrStmt> Chain(IReadOnlyList<KWhenEntry> entries, int from, IrExpr? subject, Action<KNode, List<IrStmt>> branch) {
    var statements = new List<IrStmt>();
    if (from >= entries.Count)
      return statements;

    var entry = entries[from];
    var body = new List<IrStmt>();
    if (entry.Conditions.Count == 0) {
      branch(entry.Body, body);
      return body;
    }

    IrExpr? condition = null;
    foreach (var test in entry.Conditions) {
      var lowered = this.Test(test, subject);
      condition = condition is null ? lowered : new IrBinary(IrBinaryOp.LogicalOr, condition, lowered);
    }

    this.Flush(statements);
    branch(entry.Body, body);
    var rest = this.Chain(entries, from + 1, subject, branch);
    statements.Add(new IrIf(condition!, new IrBlock(body), rest.Count == 0 ? null : new IrBlock(rest)));
    return statements;
  }

  /// <summary>One condition of a <c>when</c>: equality with the subject, a range test, or a truth value without a subject.</summary>
  private IrExpr Test(KExpression condition, IrExpr? subject) {
    if (subject is null)
      return this.Condition(condition);

    if (condition is KWhenRange range) {
      var inside = this.Contains(range.Range, subject, range);
      return range.Negated ? new IrUnary(IrUnaryOp.LogicalNot, inside) : inside;
    }

    return this.Operator(subject, "==", this.Expression(condition, subject.Type), condition);
  }

  /// <summary><c>value in range</c>: <c>first &lt;= value &amp;&amp; value &lt;= last</c> of a range, the end exclusive for <c>until</c> and <c>..&lt;</c>.</summary>
  private IrExpr Contains(KExpression range, IrExpr value, KNode at) {
    if (this.RangeOf(range) is not { Step: null } bounds)
      throw Refuse(KotlinFrontend.Control, at, "an in test of anything but a range is outside the imported subset");

    var (low, high) = bounds.Down ? (bounds.End, bounds.First) : (bounds.First, bounds.End);
    var lower = this.Operator(this.Expression(low, value.Type), "<=", value, at);
    var upper = this.Operator(value, bounds.Inclusive ? "<=" : "<", this.Expression(high, value.Type), at);
    return new IrBinary(IrBinaryOp.LogicalAnd, lower, upper);
  }

  #endregion

}
