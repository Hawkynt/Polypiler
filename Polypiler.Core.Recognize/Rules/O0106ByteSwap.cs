using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0106: shifts and masks that assemble the bytes of a 16-, 32- or 64-bit value in reverse order
/// become <see cref="IrIntrinsicOp.ByteSwap"/> - in one expression, or in a run of assignments to one
/// variable such as the two-step swap of halves and of bytes.
/// <para>
/// The value is evaluated as weighted bits (<see cref="WeightEvaluator"/>); it is a byte swap when it
/// consists of exactly the bits of one atom of its own width, bit <c>8k + j</c> landing on bit
/// <c>8(n - 1 - k) + j</c>. A mask that drops or duplicates a bit, a byte in the wrong place, an
/// arithmetic shift that drags in copies of the sign, or a second value make it something else. The
/// swapped value has no effect and cannot trap.
/// </para>
/// </summary>
public sealed class O0106ByteSwap : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0106ByteSwap() : base("O0106", "byte swap from shifts and masks", runs: true) { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    if (!Images.IsCandidate(expression) || WeightEvaluator.Evaluate(expression, context) is not { } value
      || Images.Permuted(value, (bit, width) => (width / 8 - 1 - bit / 8) * 8 + bit % 8) is not { } atom
      || atom.Type.Bits < 16 || !IrShape.IsTrapFree(atom))
      return null;

    return Images.Fit(Images.Call(IrIntrinsicOp.ByteSwap, atom.Type, atom), expression.Type);
  }

}
