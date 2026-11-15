using System.Collections.Generic;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>
/// <c>for</c> over an integer range, in each of its forms. The bounds - and the step - are evaluated
/// once, start before end, before the first iteration, and a step of zero panics there even when the
/// range is empty. The counter never computes a value past the range: an inclusive range ending at
/// the type's maximum stops on reaching it instead of testing <c>i &lt;= b</c> after an increment
/// that would overflow, and the distance to the end is measured as a <c>u64</c>, where it always fits.
/// The loop variable is a fresh copy of the counter in every iteration, so the body cannot change the
/// number of iterations, and <c>continue</c> runs the step of the IR's <c>for</c>.
/// </summary>
internal sealed partial class FunctionLowering {

  private IrExpr? For(ForExpr loop) {
    var range = this._typed.Ranges[loop];
    var element = this._typed.TypeOf(range.Start).Ir;
    var bounds = this.Operands(range.Start, range.End);
    var start = this.Once(bounds[0], "start");
    var end = this.Once(bounds[1], "end");
    IrExpr? step = null;
    if (range.Step is not null) {
      step = this.Once(this.Value(range.Step), "step");
      if (step is not IrConst { Value: not 0 })
        this.Emit(new IrIf(Equal(step, new IrConst(IrType.U64, 0)), new IrBlock([RustTraps.PanicStatement("assertion failed: step != 0")]), null));
    }

    var empty = Not(Compare(range.Inclusive ? IrBinaryOp.LessOrEqual : IrBinaryOp.Less, start, end));
    IrLocal counter;
    IrExpr condition;
    var advance = new List<IrStmt>();
    IrLocal? done = null;
    if (step is null && !range.Reversed && !range.Inclusive) {
      // a..b: the counter stays below the end, so its increment never overflows.
      counter = this.Counter(start, element);
      condition = Compare(IrBinaryOp.Less, new IrLocalRef(counter), end);
      advance.Add(new IrAssign(counter, Increment(counter)));
    } else {
      done = this.NewLocal("done", IrType.Bool);
      this.Emit(new IrDeclare(done, empty));
      var first = this.First(range, start, end, step, element);
      counter = this.Counter(first, element);
      condition = Not(new IrLocalRef(done));
      var current = new IrLocalRef(counter);
      // Whether the counter is at the last value of the range: the distance left is less than one step.
      var stride = step ?? new IrConst(IrType.U64, 1);
      var left = range.Reversed ? Distance(start, current) : Distance(current, end);
      var last = range.Inclusive || range.Reversed ? Compare(IrBinaryOp.Less, left, stride) : Compare(IrBinaryOp.LessOrEqual, left, stride);
      if (range.Reversed && !range.Inclusive && step is null)
        last = Equal(current, start);

      advance.Add(new IrAssign(done, last));
      advance.Add(new IrAssign(counter, Step(counter, stride, !range.Reversed)));
    }

    return this.Breakable(loop, _ => {
      if (loop.Pattern is IdentifierPattern variable)
        this.Bind(variable, new IrLocalRef(counter));

      this.Block(loop.Body);
    }, statements => new IrFor(condition, new IrBlock(statements), new IrBlock(advance)));
  }

  /// <summary>A bound read once into a local of its own, so that the body cannot change it through its variable.</summary>
  private IrExpr Once(IrExpr value, string name) => value is IrConst ? value : this.Temporary(value, name);

  /// <summary>The counter of a range loop, starting at its first value.</summary>
  private IrLocal Counter(IrExpr first, IrType type) {
    var counter = this.NewLocal("index", type);
    this.Emit(new IrDeclare(counter, first));
    return counter;
  }

  /// <summary>
  /// The first value of a range in its order: the start, the last value below the end for a
  /// reversed range, and for <c>step_by(n).rev()</c> the last value the forward steps reach,
  /// <c>start + (length - 1) / n * n</c>.
  /// </summary>
  private IrExpr First(RangeLoop range, IrExpr start, IrExpr end, IrExpr? step, IrType type) {
    if (!range.Reversed)
      return start;

    if (step is null || !range.StepBeforeReverse)
      return range.Inclusive ? end : Narrow(Binary(IrBinaryOp.Subtract, Widen(end, Promoted(type)), new IrConst(Promoted(type), 1)), type, IrConversion.Wrap);

    var span = Distance(start, end);
    if (!range.Inclusive)
      span = Binary(IrBinaryOp.Subtract, span, new IrConst(IrType.U64, 1));

    var offset = Binary(IrBinaryOp.Multiply, Binary(IrBinaryOp.Divide, span, step), step);
    return new IrConvert(type, Binary(IrBinaryOp.Add, Cast(start, IrType.U64), offset), IrConversion.Wrap);
  }

  /// <summary><c>to - from</c> as a <c>u64</c>, exact for <c>from &lt;= to</c> of any integer type.</summary>
  private static IrExpr Distance(IrExpr from, IrExpr to) => Binary(IrBinaryOp.Subtract, Cast(to, IrType.U64), Cast(from, IrType.U64));

  /// <summary>The counter moved by a stride, modulo 2^width: past the last value it is never read.</summary>
  private static IrExpr Step(IrLocal counter, IrExpr stride, bool up)
    => Narrow(Binary(up ? IrBinaryOp.Add : IrBinaryOp.Subtract, Cast(new IrLocalRef(counter), IrType.U64), Cast(stride, IrType.U64)), counter.Type, IrConversion.Wrap);

  /// <summary>The counter of <c>a..b</c> plus one, which stays below the end and never overflows.</summary>
  private static IrExpr Increment(IrLocal counter) {
    var wide = Promoted(counter.Type);
    return Narrow(Binary(IrBinaryOp.Add, Widen(new IrLocalRef(counter), wide), new IrConst(wide, 1)), counter.Type, IrConversion.Wrap);
  }

}
