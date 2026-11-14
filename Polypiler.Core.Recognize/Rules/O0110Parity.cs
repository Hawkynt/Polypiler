using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0110: the parity of a value becomes <see cref="IrIntrinsicOp.Parity"/> - the exclusive-or fold
/// by halves down to one bit (<c>x ^= x &gt;&gt; 16; … x ^= x &gt;&gt; 1; x &amp; 1</c>), in one
/// expression or as a run of assignments, and <c>PopCount(x) &amp; 1</c>.
/// <para>
/// The fold is evaluated bit by bit as exclusive ors of atom bits (<see cref="XorEvaluator"/>), which
/// is exact for shifts, masks and exclusive or. It is the parity when bit 0 is the exclusive or of
/// every bit of one atom and every other bit is zero: a fold that skips a half, or a missing final
/// mask, gives something else. The value has no effect and cannot trap.
/// </para>
/// </summary>
public sealed class O0110Parity : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0110Parity() : base("O0110", "parity from an exclusive-or fold", runs: true) { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    if (!Images.IsCandidate(expression))
      return null;

    if (Images.Peel(expression) is IrBinary { Op: IrBinaryOp.And } and && Images.IsConstant(and.Right, out var one) && one == 1
      && Images.Peel(and.Left) is IrIntrinsic { Op: IrIntrinsicOp.PopCount } count)
      return Images.Fit(Images.Call(IrIntrinsicOp.Parity, IrType.I32, count.Arguments[0]), expression.Type);

    if (XorEvaluator.Evaluate(expression, context) is not var (bits, atoms) || bits[0] is not { One: false, Keys.Count: >= 8 } low
      || bits.Skip(1).Any(bit => !bit.SameAs(XorBit.Zero)) || low.Keys.Select(key => key >> 6).Distinct().Count() != 1)
      return null;

    var atom = atoms[low.Keys[0] >> 6];
    if (atoms.Any(other => !ReferenceEquals(other, atom) && !IrShape.IsTrapFree(other)))
      return null;

    // The bits of the whole atom, or of its low half or quarter, which is the atom cut to that width.
    var width = low.Keys.Count;
    if (width is not (8 or 16 or 32 or 64) || width > atom.Type.Bits || low.Keys.Any(key => (key & 63) >= width) || !IrShape.IsTrapFree(atom))
      return null;

    var counted = width == atom.Type.Bits ? atom : new IrConvert(Images.Unsigned(width), atom);
    return Images.Fit(Images.Call(IrIntrinsicOp.Parity, IrType.I32, counted), expression.Type);
  }

}
