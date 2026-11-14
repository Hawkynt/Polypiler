using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0102: the SWAR population count - the mask-and-add steps of Hacker's Delight 5-1 on 32 or 64
/// bits, with the final multiplication or with shifts and adds - becomes
/// <see cref="IrIntrinsicOp.PopCount"/>, in one expression or as a run of assignments.
/// <para>
/// The value is evaluated as weighted bits (<see cref="WeightEvaluator"/>): a field holding the sum
/// of some bits survives a mask or a shift only when the field lies wholly inside or outside it, and
/// a sum of fields is kept only where the fields cannot carry into each other. The value is the
/// population count exactly when it ends as one field at bit 0 counting every bit of one atom of at
/// least eight bits once. A wrong mask, a shift that cuts a field, or a sum that overflows its field
/// leaves the field unknown and the rule does not apply.
/// </para>
/// </summary>
public sealed class O0102PopCountSwar : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0102PopCountSwar() : base("O0102", "population count from the SWAR sequence", runs: true) { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    if (!Images.IsCandidate(expression) || WeightEvaluator.Evaluate(expression, context) is not { } value || value.Single is not { } atom
      || value.Image.Constant != 0 || atom.Type.Bits < 8 || value.Image.Terms.Count != atom.Type.Bits || !IrShape.IsTrapFree(atom))
      return null;

    foreach (var weight in value.Image.Terms.Values)
      if (weight != 1)
        return null;

    return Images.Fit(Images.Call(IrIntrinsicOp.PopCount, IrType.I32, atom), expression.Type);
  }

}
