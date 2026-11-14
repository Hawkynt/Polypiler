using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0123: an integer kept between two constant bounds <c>lo &lt;= hi</c> becomes
/// <see cref="IrIntrinsicOp.Clamp"/> - from <c>Min(Max(x, lo), hi)</c> and <c>Max(Min(x, hi), lo)</c>
/// with the operands in either order, and from the compare-select chains
/// <c>x &lt; lo ? lo : x &gt; hi ? hi : x</c> and <c>x &gt; hi ? hi : x &lt; lo ? lo : x</c> in every
/// spelling O0120 accepts - also once O0120 has turned the inner selection into <c>Min</c> or <c>Max</c>.
/// <para>
/// <c>Clamp</c> traps when its low bound exceeds its high one; with constant bounds in order it never
/// does, and both nestings agree with it there. Bounds that are not constants, or are out of order,
/// leave the expression alone. In the chain <c>x</c> is evaluated more than once and must have no
/// effect and not trap.
/// </para>
/// </summary>
public sealed class O0123Clamp : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0123Clamp() : base("O0123", "clamp from nested minimum and maximum") { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) => expression switch {
    IrIntrinsic { Op: IrIntrinsicOp.Min or IrIntrinsicOp.Max, Arguments: [var first, var second] } outer when outer.Type.IsInteger
      => Nested(outer.Op, first, second) ?? Nested(outer.Op, second, first),
    IrConditional choice when choice.Type.IsInteger => Chain(choice),
    _ => null
  };

  /// <summary><c>outer(inner(x, k1), k2)</c>: the clamp, or <see langword="null"/>.</summary>
  private static IrExpr? Nested(IrIntrinsicOp outer, IrExpr inner, IrExpr bound) {
    if (inner is not IrIntrinsic { Op: IrIntrinsicOp.Min or IrIntrinsicOp.Max, Arguments: [var first, var second] } nested || nested.Op == outer
      || bound is not IrConst outerBound)
      return null;

    var (value, innerBound) = second is IrConst right ? (first, right) : first is IrConst left ? (second, left) : (null, null);
    if (value is null || innerBound is null)
      return null;

    // Min(Max(x, lo), hi): the inner is the maximum with the low bound.
    var (low, high) = outer == IrIntrinsicOp.Min ? (innerBound, outerBound) : (outerBound, innerBound);
    return Clamp(value, low, high);
  }

  /// <summary>
  /// <c>x &lt; lo ? lo : inner</c> with the inner the minimum of <c>x</c> and <c>hi</c>, or
  /// <c>x &gt; hi ? hi : inner</c> with the inner the maximum of <c>x</c> and <c>lo</c>.
  /// </summary>
  private static IrExpr? Chain(IrConditional choice) {
    if (choice.Condition is not IrBinary { Op: IrBinaryOp.Less or IrBinaryOp.LessOrEqual or IrBinaryOp.Greater or IrBinaryOp.GreaterOrEqual } comparison
      || choice.WhenTrue is not IrConst bound)
      return null;

    var (value, op, limitSide) = comparison.Right is IrConst ? (Images.Unwiden(comparison.Left), comparison.Op, comparison.Right)
      : (Images.Unwiden(comparison.Right), Images.Mirror(comparison.Op), comparison.Left);
    if (!Images.IsConstant(limitSide, out var compared) || compared != bound.Value || !IrShape.IsTrapFree(value) || value.Type != bound.Type)
      return null;

    var lower = op is IrBinaryOp.Less or IrBinaryOp.LessOrEqual;
    var inner = choice.WhenFalse switch {
      IrIntrinsic { Op: IrIntrinsicOp.Min or IrIntrinsicOp.Max, Arguments: [var first, var second] } nested => (nested.Op, first, second),
      IrConditional nested when O0120MinMax.Select(nested.Condition, nested.WhenTrue, nested.WhenFalse) is IrIntrinsic { Arguments: [var first, var second] } selected
        => (selected.Op, first, second),
      _ => default
    };
    if (inner.first is null || inner.Op != (lower ? IrIntrinsicOp.Min : IrIntrinsicOp.Max))
      return null;

    var innerBound = IrShape.Same(inner.first, value) ? inner.second : IrShape.Same(inner.second, value) ? inner.first : null;
    if (innerBound is not IrConst limit)
      return null;

    return lower ? Clamp(value, bound, limit) : Clamp(value, limit, bound);
  }

  private static IrExpr? Clamp(IrExpr value, IrConst low, IrConst high)
    => value.Type == low.Type && low.Type == high.Type && IrOperators.Number(low.Type, low.Value) <= IrOperators.Number(high.Type, high.Value)
      ? Images.Call(IrIntrinsicOp.Clamp, value.Type, value, low, high)
      : null;

}
