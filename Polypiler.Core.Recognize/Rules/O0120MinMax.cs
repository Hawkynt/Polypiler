using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0120: the smaller or the larger of two integers becomes <see cref="IrIntrinsicOp.Min"/> or
/// <see cref="IrIntrinsicOp.Max"/> - from <c>a &lt; b ? a : b</c> and every mirrored form
/// (<c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>, operands and branches swapped), and from the branchless
/// <c>b ^ ((a ^ b) &amp; -(a &lt; b))</c> with the mask written <c>-(c ? 1 : 0)</c>,
/// <c>0 - (c ? 1 : 0)</c> or <c>c ? -1 : 0</c>.
/// <para>
/// The comparison is in the signedness of its operands; operands widened without loss before the
/// comparison - a byte compared as an <c>int</c> - compare as the original values. Equal operands give
/// the same value either way, so <c>&lt;</c> and <c>&lt;=</c> are alike. Both operands are evaluated
/// once instead of up to twice and must have no effect and not trap. Floating-point operands, where
/// NaN and the signed zeros make the forms differ, are left alone.
/// </para>
/// </summary>
public sealed class O0120MinMax : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0120MinMax() : base("O0120", "minimum or maximum from a comparison and selection") { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) => expression switch {
    IrConditional choice => Select(choice.Condition, choice.WhenTrue, choice.WhenFalse),
    IrBinary { Op: IrBinaryOp.Xor } xor => Branchless(xor.Left, xor.Right) ?? Branchless(xor.Right, xor.Left),
    _ => null
  };

  /// <summary>
  /// The minimum or maximum that <c>condition ? whenTrue : whenFalse</c> is, or <see langword="null"/>:
  /// the condition orders the two branches.
  /// </summary>
  internal static IrExpr? Select(IrExpr condition, IrExpr whenTrue, IrExpr whenFalse) {
    if (condition is not IrBinary { Op: IrBinaryOp.Less or IrBinaryOp.LessOrEqual or IrBinaryOp.Greater or IrBinaryOp.GreaterOrEqual } comparison
      || !whenTrue.Type.IsInteger || !IrShape.IsTrapFree(whenTrue) || !IrShape.IsTrapFree(whenFalse))
      return null;

    var left = Images.Unwiden(comparison.Left);
    var right = Images.Unwiden(comparison.Right);
    var less = comparison.Op is IrBinaryOp.Less or IrBinaryOp.LessOrEqual;
    bool? smaller = (Images.SameValue(left, whenTrue) && Images.SameValue(right, whenFalse)) ? less
      : (Images.SameValue(right, whenTrue) && Images.SameValue(left, whenFalse)) ? !less
      : null;
    return smaller is { } minimum && whenTrue.Type == whenFalse.Type
      ? Images.Call(minimum ? IrIntrinsicOp.Min : IrIntrinsicOp.Max, whenTrue.Type, whenTrue, whenFalse)
      : null;
  }

  /// <summary><c>kept ^ ((a ^ b) &amp; mask)</c> with <c>kept</c> one of <c>a</c>, <c>b</c>: the other one where the mask's condition holds.</summary>
  private static IrExpr? Branchless(IrExpr kept, IrExpr masked) {
    if (masked is not IrBinary { Op: IrBinaryOp.And } and)
      return null;

    var (difference, condition) = and.Left is IrBinary { Op: IrBinaryOp.Xor } first && Mask(and.Right) is { } one ? (first, one)
      : and.Right is IrBinary { Op: IrBinaryOp.Xor } second && Mask(and.Left) is { } other ? (second, other)
      : (null, null);
    if (difference is null || condition is null)
      return null;

    var replaced = IrShape.Same(kept, difference.Left) ? difference.Right : IrShape.Same(kept, difference.Right) ? difference.Left : null;
    return replaced is null ? null : Select(condition, replaced, kept);
  }

  /// <summary>The condition of a mask that is all ones where it holds and zero elsewhere, or <see langword="null"/>.</summary>
  private static IrExpr? Mask(IrExpr mask) {
    if (Images.Negated(mask) is IrConditional negated && Images.IsConstant(negated.WhenTrue, out var one) && one == 1 && Images.IsConstant(negated.WhenFalse, out var zero) && zero == 0)
      return negated.Condition;

    return mask is IrConditional choice && Images.IsConstant(choice.WhenTrue, out var ones) && Images.IsConstant(choice.WhenFalse, out var none) && none == 0
      && ones == IrOperators.Wrap(choice.Type, (long)Weights.MaskOf(choice.Type.Bits))
      ? choice.Condition : null;
  }

}
