using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0105: bit reversal written as a swap cascade - the five steps of 32 bits, the six of 64 bits,
/// in one expression or as a run of assignments to one variable - or as a loop that moves one bit
/// per pass, becomes <see cref="IrIntrinsicOp.BitReverse"/>.
/// <para>
/// A cascade is recognized by its weighted bits (<see cref="WeightEvaluator"/>): exactly the bits of
/// one atom of its own width, bit <c>j</c> on bit <c>w - 1 - j</c>. A loop is a counted loop of
/// <c>w</c> passes (<see cref="CounterLoop"/>) whose body only assigns; it is unrolled symbolically,
/// and it is replaced when the result variable ends as the reversal of a value from before the loop
/// - its own old bits shifted out - and every other variable it writes ends as a constant. The
/// replacement assigns those final values, the counter included unless nothing reads it afterwards.
/// A host-supplied prover must confirm the actual expression against the intrinsic before either
/// form is replaced; an absent, unsupported or inconclusive proof leaves it unchanged.
/// </para>
/// </summary>
public sealed class O0105BitReverse : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0105BitReverse() : base("O0105", "bit reversal from a swap cascade or a bit loop", runs: true) { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    if (!Images.IsCandidate(expression) && expression is not IrIntrinsic { Op: IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight })
      return null;

    if (Reversed(expression, context) is not { } atom) {
      PermutationGate.Refused(expression, context, false);
      return null;
    }

    return PermutationGate.WithinBudget(PermutationGate.Nodes(expression, context), atom.Type.Bits, context)
      ? context.Confirm(this.Id, expression, Images.Fit(Images.Call(IrIntrinsicOp.BitReverse, atom.Type, atom), expression.Type), $"ReverseBits.u{atom.Type.Bits} from cascade") : null;
  }

  private static IrExpr? Reversed(IrExpr expression, RuleContext context)
    => WeightEvaluator.Evaluate(expression, context) is { } value && Images.Permuted(value, (bit, width) => width - 1 - bit) is { } atom
      && atom.Type.Bits >= 8 && IrShape.IsTrapFree(atom) ? atom : null;

  /// <summary>Recognizes a loop that reverses one bit per pass.</summary>
  protected override bool TryStatements(IReadOnlyList<IrStmt> statements, int index, RuleContext context, [NotNullWhen(true)] out StatementRewrite? rewrite) {
    if (CounterLoops.TryReplace(statements, index, context, (local, value) => Reversed(value, context) is { } atom && atom.Type.Bits <= local.Type.Bits
      ? context.Confirm(this.Id, value, Images.Fit(Images.Call(IrIntrinsicOp.BitReverse, atom.Type, atom), local.Type), $"ReverseBits.u{atom.Type.Bits} from loop") : null, out rewrite))
      return true;

    PermutationGate.LoopRefused(statements, index, context, this.Id, 1);
    return false;
  }

}
