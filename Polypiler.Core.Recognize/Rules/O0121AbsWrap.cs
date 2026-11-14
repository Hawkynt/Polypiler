using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0121: the absolute value of a signed 32- or 64-bit integer becomes
/// <see cref="IrIntrinsicOp.AbsWrap"/> - from <c>x &lt; 0 ? -x : x</c> and its mirrored forms
/// (<c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>, <c>0 - x</c>), and from <c>(x ^ m) - m</c> and
/// <c>(x + m) ^ m</c> with the sign mask <c>m = x &gt;&gt; (w - 1)</c>, also through a local that holds it.
/// <para>
/// Every one of these idioms wraps on the smallest value, whose negation is itself, so the result is
/// <c>AbsWrap</c> and never the trapping <c>Abs</c>. A negation that traps (<c>checked</c>) is not
/// matched, nor is a logical shift, which is no sign mask. <c>x</c> has no effect and cannot trap.
/// </para>
/// </summary>
public sealed class O0121AbsWrap : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0121AbsWrap() : base("O0121", "wrapping absolute value from a sign mask or a selection") { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    var value = expression switch {
      IrConditional choice => Selected(choice),
      IrBinary { Op: IrBinaryOp.Subtract, Left: IrBinary { Op: IrBinaryOp.Xor } xor } subtract => Masked(xor.Left, xor.Right, subtract.Right, context),
      IrBinary { Op: IrBinaryOp.Xor, Left: IrBinary { Op: IrBinaryOp.Add } add } xor => Masked(add.Left, add.Right, xor.Right, context),
      _ => null
    };
    return value is not null && value.Type.IsWideInteger && value.Type.IsSigned && IrShape.IsTrapFree(value)
      ? Images.Call(IrIntrinsicOp.AbsWrap, value.Type, value)
      : null;
  }

  /// <summary><c>x &lt; 0 ? -x : x</c> and its mirrored forms: the <c>x</c>, or <see langword="null"/>.</summary>
  private static IrExpr? Selected(IrConditional choice) {
    if (choice.Condition is not IrBinary { Op: IrBinaryOp.Less or IrBinaryOp.LessOrEqual or IrBinaryOp.Greater or IrBinaryOp.GreaterOrEqual } comparison)
      return null;

    var (value, op) = Images.IsConstant(comparison.Right, out var right) && right == 0 ? (comparison.Left, comparison.Op)
      : Images.IsConstant(comparison.Left, out var left) && left == 0 ? (comparison.Right, Images.Mirror(comparison.Op))
      : (null, comparison.Op);
    if (value is null)
      return null;

    var negative = op is IrBinaryOp.Less or IrBinaryOp.LessOrEqual;
    var (negated, kept) = negative ? (choice.WhenTrue, choice.WhenFalse) : (choice.WhenFalse, choice.WhenTrue);
    return Images.Negated(negated) is { } operand && IrShape.Same(operand, value) && IrShape.Same(kept, value) ? value : null;
  }

  /// <summary><c>(x ^ m) - m</c> or <c>(x + m) ^ m</c> with the sign mask of <c>x</c>: the <c>x</c>, or <see langword="null"/>.</summary>
  private static IrExpr? Masked(IrExpr value, IrExpr first, IrExpr second, RuleContext context) {
    if (!IrShape.Same(first, second) || !IrShape.IsTrapFree(first))
      return null;

    return Images.Through(first, context) is IrBinary { Op: IrBinaryOp.ShiftRight, Type.IsSigned: true } shift && Images.IsConstant(shift.Right, out var count)
      && count == shift.Type.Bits - 1 && IrShape.Same(shift.Left, value) ? value : null;
  }

}
