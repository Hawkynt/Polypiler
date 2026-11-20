using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
/// Pure counted byte loops are unrolled and their live exit values preserved. Both expressions
/// and loops require the host's equivalence proof; no prover or an inconclusive result refuses them.
/// </para>
/// </summary>
public sealed class O0106ByteSwap : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0106ByteSwap() : base("O0106", "byte swap from shifts and masks", runs: true) { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) => this.Candidate(expression, context, "expression");

  private IrExpr? Candidate(IrExpr expression, RuleContext context, string form) {
    if ((!Images.IsCandidate(expression) && expression is not IrIntrinsic { Op: IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight }) || WeightEvaluator.Evaluate(expression, context) is not { } value
      || Images.Permuted(value, (bit, width) => (width / 8 - 1 - bit / 8) * 8 + bit % 8) is not { } atom
      || atom.Type.Bits < 16 || !IrShape.IsTrapFree(atom)) {
      if (Images.IsCandidate(expression))
        PermutationGate.Refused(expression, context, true);

      return null;
    }

    return context.Confirm(this.Id, expression, Images.Fit(Images.Call(IrIntrinsicOp.ByteSwap, atom.Type, atom), expression.Type), $"ReverseBytes.u{atom.Type.Bits} from {form}");
  }

  /// <summary>Unrolls a pure counted byte loop and proves its final accumulator value.</summary>
  protected override bool TryStatements(IReadOnlyList<IrStmt> statements, int index, RuleContext context, [NotNullWhen(true)] out StatementRewrite? rewrite) {
    if (CounterLoops.TryReplace(statements, index, context, (local, value) => this.Candidate(value, context, "loop"), out rewrite))
      return true;

    PermutationGate.LoopRefused(statements, index, context, this.Id, 8);
    return false;
  }

}
