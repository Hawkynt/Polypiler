using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize;

/// <summary>Replaces a counted loop by the final values of the variables it writes.</summary>
internal static class CounterLoops {

  /// <summary>
  /// Unrolls the counted loop at a position (<see cref="CounterLoop"/>) and replaces it when every
  /// variable its body writes ends unchanged, as a constant, or as a value the rule recognizes - at
  /// least one of them the latter. The replacement assigns the final values from the values before
  /// the loop; the counter gets its final constant, or disappears with its initialization when
  /// nothing reads it afterwards.
  /// </summary>
  /// <param name="statements">The statements of the block.</param>
  /// <param name="index">The position of the counter's initialization.</param>
  /// <param name="context">The facts of the function.</param>
  /// <param name="recognize">The final value of a variable in the rule's operation, or <see langword="null"/>.</param>
  /// <param name="rewrite">The replacement of the initialization and the loop.</param>
  /// <returns><see langword="true"/> when the loop is replaced.</returns>
  public static bool TryReplace(IReadOnlyList<IrStmt> statements, int index, RuleContext context, Func<IrLocal, IrExpr, IrExpr?> recognize, out StatementRewrite? rewrite) {
    rewrite = null;
    if (CounterLoop.At(statements, index, context) is not { } loop || loop.Passes() is not { } passes)
      return false;

    var finals = new Dictionary<IrLocal, IrExpr>();
    var recognized = false;
    foreach (var (local, value) in loop.Unroll(passes, context)) {
      if (value is IrLocalRef reference && reference.Local == local)
        continue;

      if (HasChoice(value))
        return false;

      if (recognize(local, value) is { } operation) {
        finals[local] = operation;
        recognized = true;
      } else if (WeightEvaluator.Evaluate(value, context) is { Image.IsConstant: true } constant) {
        finals[local] = Images.Constant(local.Type, (long)constant.Image.Constant);
      } else if (Shifted(loop, local, value, passes, context) is { } shifted) {
        finals[local] = shifted;
      } else {
        return false;
      }
    }

    if (!recognized || CounterLoop.Ordered(finals) is not { } assignments)
      return false;

    var dead = context.IsDeadAfter(loop.Counter, statements, index + 2);
    rewrite = dead
      ? new(2, assignments)
      : new(2, [statements[index], .. assignments, new IrAssign(loop.Counter, CounterLoop.ValueOf(loop.Counter, loop.Start + passes))]);
    return true;
  }

  /// <summary>
  /// The final value of a variable the body shifts by a constant in every pass, as one shift of its
  /// value before the loop - by the sum of the passes' shifts, or by the width less one for an
  /// arithmetic shift that has shifted out every bit - when its bits say it is that; <see langword="null"/> otherwise.
  /// </summary>
  private static IrExpr? Shifted(CounterLoop loop, IrLocal local, IrExpr value, int passes, RuleContext context) {
    if (loop.Body.Where(assignment => assignment.Local == local).ToList() is not [{ Value: IrBinary { Op: IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical, Left: IrLocalRef self, Right: IrConst count } shift }]
      || self.Local != local || loop.Known.ContainsKey(local))
      return null;

    var width = local.Type.Bits;
    var total = System.Math.Min(count.Value * passes, width - 1);
    IrExpr candidate = new IrBinary(shift.Op, new IrLocalRef(local), Images.Constant(IrType.I32, total));
    return WeightEvaluator.Evaluate(value, context) is { } final && WeightEvaluator.Evaluate(candidate, context) is { } expected
      && final.Atoms.Count == 1 && expected.Atoms.Count == 1 && IrShape.Same(final.Atoms[0], expected.Atoms[0]) && final.Image.SameAs(expected.Image)
      ? candidate : null;
  }

  /// <summary>Whether an expression, possibly sharing nodes, holds a choice; each shared node is looked at once.</summary>
  private static bool HasChoice(IrExpr expression) {
    var seen = new HashSet<IrExpr>(ReferenceEqualityComparer.Instance);
    var pending = new Stack<IrExpr>([expression]);
    while (pending.Count > 0) {
      var node = pending.Pop();
      if (!seen.Add(node))
        continue;

      if (node is IrConditional)
        return true;

      foreach (var operand in IrShape.Operands(node))
        pending.Push(operand);
    }

    return false;
  }

}
