using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0122: the sign of an integer becomes <see cref="IrIntrinsicOp.Sign"/> - from
/// <c>(x &gt; 0 ? 1 : 0) - (x &lt; 0 ? 1 : 0)</c>, which is how C# writes <c>(x &gt; 0) - (x &lt; 0)</c>,
/// with the comparisons in either spelling, and from <c>(x &gt;&gt; (w - 1)) | (-x &gt;&gt;&gt; (w - 1))</c>
/// on a signed 32- or 64-bit <c>x</c>.
/// <para>
/// The shift-or form is exact for the smallest value too: its negation is itself, the logical shift
/// gives 1 and the arithmetic one -1, and <c>-1 | 1</c> is -1. The first shift must be arithmetic;
/// the second must be logical and of the negation, which the weighted bits of its operand decide
/// (<see cref="WeightEvaluator"/>), so <c>0u - (uint)x</c> counts as <c>-x</c>. <c>x</c> has no
/// effect and cannot trap; the result, an <c>i32</c>, is converted to the type of the expression.
/// </para>
/// </summary>
public sealed class O0122Sign : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0122Sign() : base("O0122", "sign from comparisons or shifts") { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    var value = expression switch {
      IrBinary { Op: IrBinaryOp.Subtract } difference => Compared(difference),
      IrBinary { Op: IrBinaryOp.Or } or => Shifted(or.Left, or.Right, context) ?? Shifted(or.Right, or.Left, context),
      _ => null
    };
    return value is not null && value.Type.IsInteger && IrShape.IsTrapFree(value)
      ? Images.Fit(Images.Call(IrIntrinsicOp.Sign, IrType.I32, value), expression.Type)
      : null;
  }

  /// <summary>The value a truth-as-integer <c>c ? 1 : 0</c> compares with zero, and whether it asks for positive; <see langword="null"/> otherwise.</summary>
  private static (IrExpr Value, bool Positive)? Compares(IrExpr flag) {
    if (flag is not IrConditional { Condition: IrBinary { Op: IrBinaryOp.Less or IrBinaryOp.Greater } comparison } choice
      || !Images.IsConstant(choice.WhenTrue, out var one) || one != 1 || !Images.IsConstant(choice.WhenFalse, out var zero) || zero != 0)
      return null;

    if (Images.IsConstant(comparison.Right, out var right) && right == 0)
      return (comparison.Left, comparison.Op == IrBinaryOp.Greater);

    return Images.IsConstant(comparison.Left, out var left) && left == 0 ? (comparison.Right, comparison.Op == IrBinaryOp.Less) : null;
  }

  private static IrExpr? Compared(IrBinary difference)
    => Compares(difference.Left) is (var positive, true) && Compares(difference.Right) is (var negative, false) && IrShape.Same(positive, negative)
      ? positive : null;

  private static IrExpr? Shifted(IrExpr arithmetic, IrExpr logical, RuleContext context) {
    if (arithmetic is not IrBinary { Op: IrBinaryOp.ShiftRight, Type: { IsSigned: true, IsWideInteger: true } } sign
      || !Images.IsConstant(sign.Right, out var top) || top != sign.Type.Bits - 1)
      return null;

    var value = sign.Left;
    var width = sign.Type.Bits;
    return Images.Peel(logical) is IrBinary { Op: IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical } shift && (shift.Op == IrBinaryOp.ShiftRightLogical || !shift.Type.IsSigned)
      && shift.Type.Bits == width && Images.IsConstant(shift.Right, out var count) && count == top
      && Images.Computes(shift.Left, value, bits => bits.Negate(), context)
      ? value : null;
  }

}
