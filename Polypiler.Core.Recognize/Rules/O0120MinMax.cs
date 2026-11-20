using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0120: the smaller or the larger of two integers becomes <see cref="IrIntrinsicOp.Min"/> or
/// <see cref="IrIntrinsicOp.Max"/>, from three forms:
/// <list type="bullet">
/// <item><em>compare-select</em>: <c>a &lt; b ? a : b</c> and every mirrored form (<c>&lt;=</c>,
/// <c>&gt;</c>, <c>&gt;=</c>, operands and branches swapped);</item>
/// <item><em>mask-select</em>: <c>b ^ ((a ^ b) &amp; -(a &lt; b))</c> with the mask written
/// <c>-(c ? 1 : 0)</c>, <c>0 - (c ? 1 : 0)</c> or <c>c ? -1 : 0</c>, and the operands of each
/// <c>^</c> and <c>&amp;</c> in either order;</item>
/// <item><em>subtract-sign</em>: <c>b + (d &amp; (d &gt;&gt; (w - 1)))</c> for the minimum and
/// <c>a - (d &amp; (d &gt;&gt; (w - 1)))</c> for the maximum, with <c>d = a - b</c>, also through a
/// stable local.</item>
/// </list>
/// <para>
/// The comparison is in the signedness of its operands; operands widened without loss compare as the
/// original values. Every replacement requires a host proof of the actual expression, so the
/// subtract-sign form is rewritten only where <c>a - b</c> cannot overflow - for example for operands
/// widened from a narrower type - and is otherwise refused as <c>subtraction may overflow</c>. Checked
/// subtraction, a logical sign shift, a mask of a comparison of other values and operands that may
/// trap or act are refused. Floating-point operands, where NaN and the signed zeros make the forms
/// differ, are left alone.
/// </para>
/// </summary>
public sealed class O0120MinMax : ExpressionRule {

  private const string NotMinMax = "not equivalent to Min or Max";

  /// <summary>Creates the rule.</summary>
  public O0120MinMax() : base("O0120", "minimum or maximum from a comparison and selection") { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    if (!expression.Type.IsInteger)
      return null;

    var (candidate, form, reason) = expression switch {
      IrConditional choice => (Ordered(choice.Condition, choice.WhenTrue, choice.WhenFalse), "compare-select", null),
      IrBinary { Op: IrBinaryOp.Xor } xor => Masked(xor),
      IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.SubtractChecked } sum => Subtracted(sum, context),
      _ => (null, "", null)
    };
    if (candidate is not IrIntrinsic { Arguments: [var first, _] } after) {
      if (reason is not null)
        context.Note?.Invoke($"O0120 not applied: {reason}");
      return null;
    }

    if (!IrShape.IsTrapFree(expression)) {
      context.Note?.Invoke("O0120 not applied: operand may trap or have effects");
      return null;
    }

    var name = (first.Type.IsSigned ? "S" : "U") + after.Op;
    return context.Confirm(this.Id, expression, after, $"{name}.{after.Type} from {form}",
      form == "subtract-sign" ? "subtraction may overflow" : null);
  }

  /// <summary>
  /// The minimum or maximum that <c>condition ? whenTrue : whenFalse</c> is, or <see langword="null"/>:
  /// the condition orders the two branches. O0123 asks this of the inner selection of a clamp.
  /// </summary>
  internal static IrExpr? Select(IrExpr condition, IrExpr whenTrue, IrExpr whenFalse)
    => IrShape.IsTrapFree(whenTrue) && IrShape.IsTrapFree(whenFalse) ? Ordered(condition, whenTrue, whenFalse) : null;

  /// <summary>
  /// <see cref="Select"/> without the effect check, which the rule makes on the whole expression so that it can report it.
  /// Conversions that keep every value are looked through on both sides: they keep the order too.
  /// </summary>
  private static IrExpr? Ordered(IrExpr condition, IrExpr whenTrue, IrExpr whenFalse) {
    if (condition is not IrBinary { Op: IrBinaryOp.Less or IrBinaryOp.LessOrEqual or IrBinaryOp.Greater or IrBinaryOp.GreaterOrEqual } comparison
      || !whenTrue.Type.IsInteger || whenTrue.Type != whenFalse.Type)
      return null;

    var left = Images.Unwiden(comparison.Left);
    var right = Images.Unwiden(comparison.Right);
    var (first, second) = (Images.Unwiden(whenTrue), Images.Unwiden(whenFalse));
    var less = comparison.Op is IrBinaryOp.Less or IrBinaryOp.LessOrEqual;
    bool? smaller = Images.SameValue(left, first) && Images.SameValue(right, second) ? less
      : Images.SameValue(right, first) && Images.SameValue(left, second) ? !less
      : null;
    return smaller is { } minimum ? Images.Call(minimum ? IrIntrinsicOp.Min : IrIntrinsicOp.Max, whenTrue.Type, whenTrue, whenFalse) : null;
  }

  /// <summary><c>kept ^ ((a ^ b) &amp; mask)</c> with <c>kept</c> one of <c>a</c>, <c>b</c>, in every operand order.</summary>
  private static (IrExpr? Candidate, string Form, string? Reason) Masked(IrBinary xor) {
    var shaped = false;
    foreach (var (kept, masked) in new[] { (xor.Left, xor.Right), (xor.Right, xor.Left) }) {
      if (masked is not IrBinary { Op: IrBinaryOp.And } and)
        continue;

      foreach (var (difference, mask) in new[] { (and.Left, and.Right), (and.Right, and.Left) }) {
        if (difference is not IrBinary { Op: IrBinaryOp.Xor } pair || Mask(mask) is not { } condition)
          continue;

        var replaced = IrShape.Same(kept, pair.Left) ? pair.Right : IrShape.Same(kept, pair.Right) ? pair.Left : null;
        if (replaced is null)
          continue;

        shaped = true;
        if (Ordered(condition, replaced, kept) is { } selected)
          return (selected, "mask-select", null);
      }
    }

    return (null, "", shaped ? "mask is not derived from the compared operands" : null);
  }

  /// <summary>The condition of a mask that is all ones where it holds and zero elsewhere, or <see langword="null"/>.</summary>
  private static IrExpr? Mask(IrExpr mask) {
    if (Images.Negated(mask) is IrConditional negated && Images.IsConstant(negated.WhenTrue, out var one) && one == 1 && Images.IsConstant(negated.WhenFalse, out var zero) && zero == 0)
      return negated.Condition;

    return mask is IrConditional choice && Images.IsConstant(choice.WhenTrue, out var ones) && Images.IsConstant(choice.WhenFalse, out var none) && none == 0
      && ones == IrOperators.Wrap(choice.Type, (long)Weights.MaskOf(choice.Type.Bits))
      ? choice.Condition : null;
  }

  /// <summary><c>b + (d &amp; (d &gt;&gt; (w - 1)))</c> or <c>a - (d &amp; (d &gt;&gt; (w - 1)))</c> with <c>d = a - b</c>.</summary>
  private static (IrExpr? Candidate, string Form, string? Reason) Subtracted(IrBinary sum, RuleContext context) {
    var pairs = sum.Op == IrBinaryOp.Add ? new[] { (sum.Left, sum.Right), (sum.Right, sum.Left) } : new[] { (sum.Left, sum.Right) };
    foreach (var (outer, masked) in pairs) {
      if (masked is not IrBinary { Op: IrBinaryOp.And } and)
        continue;

      foreach (var (first, second) in new[] { (and.Left, and.Right), (and.Right, and.Left) }) {
        if (Images.Through(first, context) is not IrBinary { Op: IrBinaryOp.Subtract or IrBinaryOp.SubtractChecked } difference
          || Images.Peel(Images.Through(second, context)) is not IrBinary { Op: IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical } shift
          || !IrShape.Same(Images.Through(Images.Peel(shift.Left), context), difference))
          continue;

        // The difference is a - b: the minimum adds the masked difference to b, the maximum subtracts it from a.
        var minimum = sum.Op == IrBinaryOp.Add;
        if (!IrShape.Same(outer, minimum ? difference.Right : difference.Left))
          continue;
        if (sum.Op == IrBinaryOp.SubtractChecked || difference.Op == IrBinaryOp.SubtractChecked)
          return (null, "", "checked arithmetic");
        if (shift.Op == IrBinaryOp.ShiftRightLogical || !shift.Type.IsSigned || !Images.IsConstant(shift.Right, out var count) || count != shift.Type.Bits - 1)
          return (null, "", NotMinMax);

        return (Images.Call(minimum ? IrIntrinsicOp.Min : IrIntrinsicOp.Max, sum.Type, difference.Left, difference.Right), "subtract-sign", null);
      }
    }

    return (null, "", null);
  }

}
