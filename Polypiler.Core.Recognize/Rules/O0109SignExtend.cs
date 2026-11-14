using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0109: sign extension of the low <c>b</c> bits becomes <see cref="IrIntrinsicOp.SignExtend"/>.
/// <list type="bullet">
/// <item><c>(x &lt;&lt; k) &gt;&gt; k</c> with a constant <c>0 &lt; k &lt; w</c> and an arithmetic right
/// shift - the shift of a signed value - is <c>SignExtend(x, w - k)</c>. A logical right shift
/// extends with zeros and is left alone.</item>
/// <item><c>(x ^ m) - m</c> with <c>m = 1 &lt;&lt; (b - 1)</c> is <c>SignExtend(x, b)</c> only when the bits of
/// <c>x</c> from <c>b</c> on are known to be zero - a mask, a zero-extended byte - which the weighted
/// bits of <c>x</c> show (<see cref="WeightEvaluator"/>); otherwise the high bits pass through and
/// the rule does not apply.</item>
/// </list>
/// </summary>
public sealed class O0109SignExtend : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0109SignExtend() : base("O0109", "sign extension from a shift pair or xor-subtract") { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    if (expression is not IrBinary { Type.IsWideInteger: true } binary)
      return null;

    var width = binary.Type.Bits;
    if (binary is { Op: IrBinaryOp.ShiftRight, Type.IsSigned: true, Left: IrBinary { Op: IrBinaryOp.ShiftLeft } inner }
      && Images.IsConstant(binary.Right, out var right) && Images.IsConstant(inner.Right, out var left) && right == left && right > 0 && right < width)
      return Images.Call(IrIntrinsicOp.SignExtend, binary.Type, inner.Left, Images.Constant(IrType.I32, width - right));

    if (binary is not { Op: IrBinaryOp.Subtract, Left: IrBinary { Op: IrBinaryOp.Xor } xor } || !Images.IsConstant(binary.Right, out var mask)
      || !Images.IsConstant(xor.Right, out var flip) || mask != flip)
      return null;

    var pattern = (ulong)mask & Weights.MaskOf(width);
    var bits = System.Numerics.BitOperations.TrailingZeroCount(pattern) + 1;
    if ((pattern & (pattern - 1)) != 0 || pattern == 0 || bits >= width || WeightEvaluator.Evaluate(xor.Left, context)?.Image.Decompose() is not { } fields)
      return null;

    foreach (var field in fields)
      if (field.End > bits)
        return null;

    return Images.Call(IrIntrinsicOp.SignExtend, binary.Type, xor.Left, Images.Constant(IrType.I32, bits));
  }

}
