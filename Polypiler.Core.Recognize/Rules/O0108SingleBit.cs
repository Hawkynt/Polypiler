using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0108: the single-bit idioms with a position <c>i</c> become <see cref="IrIntrinsicOp.TestBit"/>,
/// <see cref="IrIntrinsicOp.SetBit"/>, <see cref="IrIntrinsicOp.ClearBit"/> and
/// <see cref="IrIntrinsicOp.ToggleBit"/>: <c>((x &gt;&gt; i) &amp; 1) != 0</c> (also <c>== 0</c>,
/// <c>== 1</c>), <c>(x &amp; (1 &lt;&lt; i)) != 0</c>, <c>x | (1 &lt;&lt; i)</c>,
/// <c>x &amp; ~(1 &lt;&lt; i)</c> and <c>x ^ (1 &lt;&lt; i)</c> on a 32- or 64-bit value.
/// <para>
/// A shift of the IR takes its count modulo the width while the operations trap on a position
/// outside it, so the position becomes <c>i &amp; (w - 1)</c> - unless it is a constant, or is already
/// masked so. The constant one and the value have the same width; conversions between the signed and
/// unsigned type of one width change no bit and are looked through. A value narrower than 32 bits,
/// which C# and JavaScript widen before shifting, is left alone: there a position beyond its width
/// gives a result instead of a trap. Value and position have no effect and cannot trap.
/// </para>
/// </summary>
public sealed class O0108SingleBit : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0108SingleBit() : base("O0108", "single-bit test, set, clear or toggle") { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) => expression switch {
    IrBinary { Op: IrBinaryOp.NotEqual or IrBinaryOp.Equal } comparison => Test(comparison),
    IrBinary { Op: IrBinaryOp.Or or IrBinaryOp.Xor } binary => Change(binary, binary.Op == IrBinaryOp.Or ? IrIntrinsicOp.SetBit : IrIntrinsicOp.ToggleBit, false),
    IrBinary { Op: IrBinaryOp.And } binary => Change(binary, IrIntrinsicOp.ClearBit, true),
    _ => null
  };

  /// <summary>The position of a single bit <c>1 &lt;&lt; i</c> of a width, or <see langword="null"/>.</summary>
  private static IrExpr? Single(IrExpr expression, int width)
    => Images.Peel(expression) is IrBinary { Op: IrBinaryOp.ShiftLeft, Left: IrConst { Value: 1 } one } shift && one.Type.Bits == width ? shift.Right : null;

  /// <summary>The position as the operations take it: a constant or a count masked to the width.</summary>
  private static IrExpr? Position(IrExpr position, int width) {
    if (!IrShape.IsTrapFree(position))
      return null;

    if (position is IrConst constant)
      return Images.Constant(IrType.I32, constant.Value & (width - 1));

    return position is IrBinary { Op: IrBinaryOp.And, Right: IrConst mask } && mask.Value == width - 1
      ? position
      : new IrBinary(IrBinaryOp.And, position, Images.Constant(IrType.I32, width - 1));
  }

  private static IrExpr? Test(IrBinary comparison) {
    if (!Images.IsConstant(comparison.Right, out var compared) || compared is not (0 or 1) || Images.Peel(comparison.Left) is not IrBinary { Op: IrBinaryOp.And } and)
      return null;

    var (value, position, shifted) = (Images.Peel(and.Left), Images.Peel(and.Right)) switch {
      (IrBinary { Op: IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical } shift, IrConst { Value: 1 }) => (Images.Peel(shift.Left), shift.Right, true),
      (IrConst { Value: 1 }, IrBinary { Op: IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical } shift) => (Images.Peel(shift.Left), shift.Right, true),
      (var left, var right) when Single(right, left.Type.Bits) is { } bit => (left, bit, false),
      (var left, var right) when Single(left, right.Type.Bits) is { } bit => (right, bit, false),
      _ => (null, null, false)
    };
    if (value is null || position is null || !value.Type.IsWideInteger || value.Type.Bits != and.Type.Bits || !IrShape.IsTrapFree(value)
      || (compared == 1 && !shifted) || Position(position, value.Type.Bits) is not { } where)
      return null;

    IrExpr test = Images.Call(IrIntrinsicOp.TestBit, IrType.Bool, value, where);
    var set = (comparison.Op == IrBinaryOp.NotEqual) == (compared == 0);
    return set ? test : new IrUnary(IrUnaryOp.LogicalNot, test);
  }

  private static IrExpr? Change(IrBinary binary, IrIntrinsicOp op, bool cleared) {
    if (!binary.Type.IsWideInteger)
      return null;

    IrExpr? Bit(IrExpr operand) => cleared
      ? Images.Peel(operand) is IrUnary { Op: IrUnaryOp.BitNot } complement ? Single(complement.Operand, binary.Type.Bits) : null
      : Single(operand, binary.Type.Bits);

    var (value, position) = Bit(binary.Right) is { } right ? (binary.Left, right) : Bit(binary.Left) is { } left ? (binary.Right, left) : (null, null);
    if (value is null || position is null || !IrShape.IsTrapFree(value) || Position(position, binary.Type.Bits) is not { } where)
      return null;

    return Images.Call(op, binary.Type, value, where);
  }

}
