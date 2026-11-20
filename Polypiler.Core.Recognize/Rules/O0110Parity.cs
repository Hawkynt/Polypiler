using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0110: the parity of a value becomes <see cref="IrIntrinsicOp.Parity"/>, from three forms:
/// <list type="bullet">
/// <item><em>xor-fold</em>: the exclusive-or fold by halves down to one bit
/// (<c>x ^= x &gt;&gt; 16; … x ^= x &gt;&gt; 1; x &amp; 1</c>), in one expression or as a run of
/// assignments;</item>
/// <item><em>nibble-lookup</em>: the fold down to four bits, then the parity of the nibble from the
/// sixteen-bit table <c>(0x6996 &gt;&gt; (x &amp; 0xF)) &amp; 1</c>;</item>
/// <item><em>popcount-mask</em>: <c>PopCount(x) &amp; 1</c>.</item>
/// </list>
/// <para>
/// The fold is evaluated bit by bit as exclusive ors of atom bits (<see cref="XorEvaluator"/>), which
/// is exact for shifts, masks and exclusive or, an arithmetic shift of a signed value included. It is
/// the parity when bit 0 is the exclusive or of every bit of one atom - or of its low 8, 16 or 32 bits -
/// and every other bit is zero. The table is read entry by entry: it is the parity table when entry
/// <c>v</c> is the parity of <c>v</c>. Every replacement requires a host proof of the actual expression.
/// A fold-shaped expression of one value that is not its parity - a skipped step, a wider final mask,
/// the complement table <c>0x9669</c> - is refused as <c>not equivalent to parity</c>, and a fold
/// larger than <c>4·log2 w + 4</c> operations as <c>expression too large</c>. The value has no effect
/// and cannot trap.
/// </para>
/// </summary>
public sealed class O0110Parity : ExpressionRule {

  private const string NotParity = "not equivalent to parity";

  /// <summary>The parity of each nibble, entry <c>v</c> at bit <c>v</c>.</summary>
  private const long NibbleTable = 0x6996;

  /// <summary>Creates the rule.</summary>
  public O0110Parity() : base("O0110", "parity from an exclusive-or fold", runs: true) { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    if (!Images.IsCandidate(expression))
      return null;

    var (value, form, reason) = Images.Peel(expression) switch {
      IrBinary { Op: IrBinaryOp.And } masked when Images.IsConstant(masked.Right, out var one) && one == 1
        && Images.Peel(masked.Left) is IrIntrinsic { Op: IrIntrinsicOp.PopCount } count => (count.Arguments[0], "popcount-mask", null),
      IrBinary { Op: IrBinaryOp.And } masked when Lookup(masked) is { } table => Nibble(table.Index, table.Bits, context),
      _ => Folded(expression, context)
    };
    if (value is null) {
      if (reason is not null)
        context.Note?.Invoke($"O0110 not applied: {reason}");
      return null;
    }

    if (!IrShape.IsTrapFree(expression)) {
      context.Note?.Invoke("O0110 not applied: operand may trap or have effects");
      return null;
    }

    var after = Images.Fit(Images.Call(IrIntrinsicOp.Parity, IrType.I32, value), expression.Type);
    return context.Confirm(this.Id, expression, after, $"Parity.{value.Type} from {form}");
  }

  /// <summary>The value whose parity bit 0 is, from the exclusive-or bits of one atom; the reason it is not; or nothing for an unrelated expression.</summary>
  private static (IrExpr? Value, string Form, string? Reason) Folded(IrExpr expression, RuleContext context) {
    if (XorEvaluator.Evaluate(expression, context) is not var (bits, atoms))
      return (null, "", null);

    var low = bits[0];
    var rest = bits.Skip(1).All(bit => bit.SameAs(XorBit.Zero));
    // A mask of the lowest one or two bits ends a fold; a wider one, such as a nibble index, does not.
    var shaped = Images.Peel(expression) is IrBinary { Op: IrBinaryOp.And } masked && Images.IsConstant(masked.Right, out var mask) && mask is > 0 and < 4;
    return Parity(low, rest, atoms, shaped, expression, "xor-fold");
  }

  /// <summary>
  /// The value whose parity an exclusive-or bit is when the other result bits are zero; a refusal when
  /// a masked expression of one value looks like a fold but is not its parity; nothing otherwise.
  /// </summary>
  private static (IrExpr? Value, string Form, string? Reason) Parity(XorBit low, bool rest, IReadOnlyList<IrExpr> atoms, bool shaped, IrExpr expression, string form) {
    // Four or more bits of one value, masked or looked up: something meant to fold it.
    var single = low.Keys.Count > 0 && low.Keys.Select(key => key >> 6).Distinct().Count() == 1;
    var meant = shaped && single && low.Keys.Count >= 4;
    if (!single || !rest || low.One) {
      // A complemented parity, other bits left over: not the parity, though it looks like it.
      return (null, "", meant ? NotParity : null);
    }

    var atom = atoms[low.Keys[0] >> 6];
    var width = low.Keys.Count;
    if (width is not (8 or 16 or 32 or 64) || width > atom.Type.Bits || low.Keys.Any(key => (key & 63) >= width))
      return (null, "", meant ? NotParity : null);

    if (Operations(expression) > 4 * Log2(width) + 4)
      return (null, "", "expression too large");

    // The bits of the whole atom, or of its low half or quarter, which is the atom cut to that width.
    var counted = width == atom.Type.Bits ? atom : new IrConvert(Images.Unsigned(width), atom);
    return (counted, form, null);
  }

  private static int Log2(int width) => width switch { 8 => 3, 16 => 4, 32 => 5, _ => 6 };

  /// <summary>The operations of an expression, each shared node once: what a fold of shared steps costs.</summary>
  private static int Operations(IrExpr expression)
    => IrShape.Nodes(expression).Distinct(ReferenceEqualityComparer.Instance).Count(node => node is IrBinary or IrUnary);

  /// <summary><c>(T &gt;&gt; (y &amp; 15)) &amp; 1</c> with a constant table <c>T</c>: the nibble <c>y</c> and the table's bits, or <see langword="null"/>.</summary>
  private static (IrExpr Index, long Bits)? Lookup(IrBinary and) {
    if (!Images.IsConstant(and.Right, out var one) || one != 1
      || Images.Peel(and.Left) is not IrBinary { Op: IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical, Left: IrConst table } shift
      || Strip(shift.Right) is not IrBinary { Op: IrBinaryOp.And } index || !Images.IsConstant(index.Right, out var nibble) || nibble != 15)
      return null;

    return (index.Left, table.Value & 0xFFFF);
  }

  /// <summary>
  /// An index without the integer conversions around it: after <c>&amp; 15</c> only its four lowest bits
  /// are left, which every conversion between integer types of at least eight bits keeps.
  /// </summary>
  private static IrExpr Strip(IrExpr expression) {
    while (expression is IrConvert { Contract: IrConversion.Wrap, Operand.Type.IsInteger: true, Type.IsInteger: true } conversion)
      expression = conversion.Operand;

    return expression;
  }

  /// <summary>The parity of a fold read through a nibble table: the table must be the parity of each nibble.</summary>
  private static (IrExpr? Value, string Form, string? Reason) Nibble(IrExpr index, long table, RuleContext context) {
    if (XorEvaluator.Evaluate(index, context) is not var (bits, atoms) || bits.Length < 4)
      return (null, "", null);

    var low = bits[0].Xor(bits[1]).Xor(bits[2]).Xor(bits[3]);
    var (value, _, reason) = Parity(low, true, atoms, true, index, "nibble-lookup");
    if (value is null)
      return (null, "", reason);

    return table == NibbleTable ? (value, "nibble-lookup", null) : (null, "", NotParity);
  }

}
