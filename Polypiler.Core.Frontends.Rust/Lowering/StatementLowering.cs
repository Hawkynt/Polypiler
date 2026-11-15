using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>
/// Blocks, <c>if</c>, loops, labels and jumps. The IR's <c>break</c> and <c>continue</c> name no
/// label, they leave the innermost loop. A jump to an outer loop or to a labelled block therefore
/// sets a flag of its target and leaves the innermost loop; after each loop it passes, the flag is
/// tested and the jump goes on, until the target is the innermost loop. A labelled block is a loop
/// that runs once. A <c>loop</c>, a labelled block and an <c>if</c> or <c>match</c> that yields a
/// value leave it in a local declared before them.
/// </summary>
internal sealed partial class FunctionLowering {

  /// <summary>A loop of the IR, made from a Rust loop or a labelled block.</summary>
  private sealed class Frame(Expr node, IrLocal? value) {

    public Expr Node { get; } = node;

    /// <summary>The local a <c>break</c> with a value leaves it in.</summary>
    public IrLocal? Value { get; } = value;

    /// <summary>The flag a <c>break</c> from an inner loop sets.</summary>
    public IrLocal? BreakFlag { get; set; }

    /// <summary>The flag a <c>continue</c> from an inner loop sets.</summary>
    public IrLocal? ContinueFlag { get; set; }

    /// <summary>The outer frames a jump from inside this one goes to.</summary>
    public HashSet<Frame> Escapes { get; } = [];

  }

  private readonly List<Frame> _frames = [];

  // ---------------------------------------------------------------- blocks and if

  private IrExpr? BlockExpression(BlockExpr block) {
    if (block.Label is null)
      return this.Block(block.Block);

    return this.Breakable(block, body => {
      var value = this.Block(block.Block);
      if (value is not null && body.Value is not null)
        this.Emit(new IrAssign(body.Value, value));
    }, statements => new IrDoWhile(new IrBlock(statements), Truth(false)));
  }

  private IrExpr? If(IfExpr conditional) {
    var type = this._typed.TypeOf(conditional);
    var wanted = type.Kind is not (RKind.Unit or RKind.Never);
    IrExpr condition;
    List<(IdentifierPattern Pattern, Item Item)> bindings = [];
    if (conditional.Pattern is { } pattern) {
      // if let: the scrutinee once, the pattern's test, its bindings in the then branch only.
      var scrutinee = this.Scrutinee(conditional.Condition, pattern);
      var scrutineeType = this._typed.TypeOf(conditional.Condition);
      condition = this.Test(pattern, scrutinee, scrutineeType) ?? Truth(true);
      bindings = [.. this.Bindings(pattern, scrutinee, scrutineeType)];
    } else {
      condition = this.Value(conditional.Condition);
    }

    var then = this.Capture(() => {
      this.Declare(bindings);
      return this.Block(conditional.Then);
    });
    var otherwise = conditional.Else is null ? (Statements: new List<IrStmt>(), Value: (IrExpr?)null, Diverged: false) : this.Capture(() => this.Lower(conditional.Else));
    if (!wanted) {
      this.Emit(new IrIf(condition, new IrBlock(then.Statements), conditional.Else is null ? null : new IrBlock(otherwise.Statements)));
      this._diverged |= then.Diverged && otherwise.Diverged;
      return null;
    }

    if (then.Statements.Count == 0 && otherwise.Statements.Count == 0 && !then.Diverged && !otherwise.Diverged)
      return new IrConditional(condition, then.Value!, otherwise.Value!);

    // One branch never ends: the other one's value is the value, computed after the if.
    if (then.Diverged) {
      this.Emit(new IrIf(condition, new IrBlock(then.Statements), null));
      this.Splice(otherwise.Statements, otherwise.Diverged);
      return otherwise.Value;
    }

    if (otherwise.Diverged) {
      this.Emit(new IrIf(Not(condition), new IrBlock(otherwise.Statements), null));
      this.Splice(then.Statements, then.Diverged);
      return then.Value;
    }

    var slot = this.Slot(type.Ir, "result");
    then.Statements.Add(new IrAssign(slot, then.Value!));
    otherwise.Statements.Add(new IrAssign(slot, otherwise.Value!));
    this.Emit(new IrIf(condition, new IrBlock(then.Statements), new IrBlock(otherwise.Statements)));
    return new IrLocalRef(slot);
  }

  // ---------------------------------------------------------------- loops

  /// <summary>
  /// Lowers a loop or a labelled block: its value local, its frame, its body, then the flags its
  /// inner jumps need, the loop, and the tests that carry jumps to outer frames on.
  /// </summary>
  private IrExpr? Breakable(Expr node, System.Action<Frame> body, System.Func<List<IrStmt>, IrStmt> loop) {
    var type = this._typed.TypeOf(node);
    return this.Breakable(node, body, loop, type.Kind is RKind.Unit or RKind.Never ? null : this.Ir(type));
  }

  /// <summary>A loop or a block that runs once whose value, if it has one, is of the given type.</summary>
  private IrExpr? Breakable(Expr node, System.Action<Frame> body, System.Func<List<IrStmt>, IrStmt> loop, IrType? valueType) {
    var value = valueType is null ? null : this.Slot(valueType, "result");
    var frame = new Frame(node, value);
    this._frames.Add(frame);
    var lowered = this.Capture(() => {
      body(frame);
      return null;
    });
    this._frames.RemoveAt(this._frames.Count - 1);
    foreach (var flag in new[] { frame.BreakFlag, frame.ContinueFlag })
      if (flag is not null)
        this.Emit(new IrDeclare(flag, Truth(false)));

    this.Emit(loop(lowered.Statements));
    this.Escaped(frame);
    return value is null ? null : new IrLocalRef(value);
  }

  /// <summary>After a loop: the jumps from inside it to outer frames go on.</summary>
  private void Escaped(Frame frame) {
    var parent = this._frames.Count > 0 ? this._frames[^1] : null;
    foreach (var target in frame.Escapes) {
      var broke = target.BreakFlag is null ? null : new IrLocalRef(target.BreakFlag);
      var continued = target.ContinueFlag is null ? null : new IrLocalRef(target.ContinueFlag);
      if (target == parent) {
        if (broke is not null)
          this.Emit(new IrIf(broke, new IrBlock([new IrBreak()]), null));

        if (continued is not null)
          this.Emit(new IrIf(continued, new IrBlock([new IrAssign(target.ContinueFlag!, Truth(false)), new IrContinue()]), null));

        continue;
      }

      var either = broke is null ? continued! : continued is null ? broke : Or(broke, continued);
      this.Emit(new IrIf(either, new IrBlock([new IrBreak()]), null));
      parent!.Escapes.Add(target);
    }
  }

  private IrExpr? Loop(LoopExpr loop) => this.Breakable(loop, _ => this.Block(loop.Body), statements => new IrWhile(Truth(true), new IrBlock(statements)));

  private IrExpr? While(WhileExpr loop) {
    IrExpr? simple = null;
    if (loop.Pattern is { } pattern) {
      // while let: every pass evaluates the scrutinee, tests the pattern, and binds it for the body.
      return this.Breakable(loop, _ => {
        var scrutinee = this.Scrutinee(loop.Condition, pattern);
        var type = this._typed.TypeOf(loop.Condition);
        if (this.Test(pattern, scrutinee, type) is { } matches)
          this.Emit(new IrIf(Not(matches), new IrBlock([new IrBreak()]), null));

        this.Declare([.. this.Bindings(pattern, scrutinee, type)]);
        this.Block(loop.Body);
      }, statements => new IrWhile(Truth(true), new IrBlock(statements)));
    }

    return this.Breakable(loop, _ => {
      var mark = this._statements.Count;
      var test = this.Value(loop.Condition);
      if (this._statements.Count == mark && !this._diverged)
        simple = test;
      else
        // A condition that needs statements is tested at the top of an endless loop.
        this.Emit(new IrIf(Not(test), new IrBlock([new IrBreak()]), null));

      this.Block(loop.Body);
    }, statements => new IrWhile(simple ?? Truth(true), new IrBlock(statements)));
  }

  // ---------------------------------------------------------------- jumps

  private IrExpr? Break(BreakExpr jump) {
    var target = this._frames.Last(frame => ReferenceEquals(frame.Node, this._typed.Targets[jump]));
    if (jump.Value is not null) {
      var value = this.Value(jump.Value);
      if (target.Value is not null)
        this.Emit(new IrAssign(target.Value, value));
    }

    this.Jump(target, false);
    return null;
  }

  private IrExpr? Continue(ContinueExpr jump) {
    this.Jump(this._frames.Last(frame => ReferenceEquals(frame.Node, this._typed.Targets[jump])), true);
    return null;
  }

  private void Jump(Frame target, bool isContinue) {
    var innermost = this._frames[^1];
    if (target == innermost) {
      this.Emit(isContinue ? new IrContinue() : new IrBreak());
    } else {
      var flag = isContinue
        ? target.ContinueFlag ??= this.NewLocal("continue" + Label(target), IrType.Bool)
        : target.BreakFlag ??= this.NewLocal("break" + Label(target), IrType.Bool);
      this.Emit(new IrAssign(flag, Truth(true)));
      this.Emit(new IrBreak());
      innermost.Escapes.Add(target);
    }

    this._diverged = true;
  }

  private static string Label(Frame frame) {
    var label = frame.Node switch {
      LoopExpr loop => loop.Label,
      WhileExpr loop => loop.Label,
      BlockExpr block => block.Label,
      _ => ((ForExpr)frame.Node).Label
    };
    return label is null ? "" : char.ToUpperInvariant(label[0]) + label[1..];
  }

  private IrExpr? Return(ReturnExpr jump) {
    var value = jump.Value is null ? null : this.Lower(jump.Value);
    if (value is not null && jump.Value is not null)
      value = this.Converted(value, this.CurrentResult, this._typed.TypeOf(jump.Value));

    if (!this._diverged)
      this.EmitReturn(value);

    this._diverged = true;
    return null;
  }

}
