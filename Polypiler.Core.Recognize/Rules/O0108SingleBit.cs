using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0108: the single-bit idioms with a position <c>i</c> become <see cref="IrIntrinsicOp.TestBit"/>,
/// <see cref="IrIntrinsicOp.SetBit"/>, <see cref="IrIntrinsicOp.ClearBit"/> and
/// <see cref="IrIntrinsicOp.ToggleBit"/>: <c>((x &gt;&gt; i) &amp; 1) != 0</c> (also <c>== 0</c>,
/// <c>== 1</c>, through an arithmetic or a logical shift), <c>(x &amp; (1 &lt;&lt; i)) != 0</c>,
/// <c>x | (1 &lt;&lt; i)</c>, <c>x &amp; ~(1 &lt;&lt; i)</c> and <c>x ^ (1 &lt;&lt; i)</c>.
/// <para>
/// A shift of the tree IR takes its count modulo the width while the operations trap on a position
/// outside it, so the position becomes <c>i &amp; (w - 1)</c> - unless it is a constant, or is already
/// masked so; with such a position the operation is total. The constant one has the width of the
/// operation; conversions between the signed and unsigned type of one width change no bit and are
/// looked through. Values narrower than 32 bits are matched on the promoted operation the frontend
/// wrote, which is where their shift happens. Every replacement requires a host proof of the actual
/// expression. A mask computed at another width and widened, a shifted constant other than one and a
/// tested field wider than one bit are refused with their reasons, as are a value or position that
/// may trap or act; a constant mask is already canonical and is left alone silently.
/// </para>
/// </summary>
public sealed class O0108SingleBit : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0108SingleBit() : base("O0108", "single-bit test, set, clear or toggle") { }

  /// <summary>What an operand is when read as a single bit <c>1 &lt;&lt; i</c>.</summary>
  private enum Shape { None, Single, OtherConstant, OtherWidth }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    var (candidate, form, reason) = expression switch {
      IrBinary { Op: IrBinaryOp.NotEqual or IrBinaryOp.Equal } comparison => Test(comparison),
      IrBinary { Op: IrBinaryOp.Or } binary => Change(binary, IrIntrinsicOp.SetBit, "or-mask", false),
      IrBinary { Op: IrBinaryOp.Xor } binary => Change(binary, IrIntrinsicOp.ToggleBit, "xor-mask", false),
      IrBinary { Op: IrBinaryOp.And } binary => Change(binary, IrIntrinsicOp.ClearBit, "and-not-mask", true),
      _ => (null, "", null)
    };
    if (candidate is null) {
      if (reason is not null)
        context.Note?.Invoke($"O0108 not applied: {reason}");
      return null;
    }

    if (!IrShape.IsTrapFree(expression)) {
      context.Note?.Invoke("O0108 not applied: operand may trap or have effects");
      return null;
    }

    var operation = (IrIntrinsic)(candidate is IrUnary { Operand: var tested } ? tested : candidate);
    return context.Confirm(this.Id, expression, candidate, $"{operation.Op}.{operation.Arguments[0].Type} from {form}");
  }

  /// <summary>An expression without any integer conversion around it.</summary>
  private static IrExpr Strip(IrExpr expression) {
    while (expression is IrConvert { Contract: IrConversion.Wrap, Operand.Type.IsInteger: true, Type.IsInteger: true } conversion)
      expression = conversion.Operand;

    return expression;
  }

  /// <summary>Reads an operand as <c>1 &lt;&lt; i</c> of a width - or as <c>~(1 &lt;&lt; i)</c> when cleared - and gives <c>i</c>.</summary>
  private static (Shape Shape, IrExpr? Position) Read(IrExpr operand, int width, bool cleared) {
    if (cleared) {
      if (Strip(operand) is not IrUnary { Op: IrUnaryOp.BitNot } complement)
        return (Shape.None, null);

      operand = complement.Operand;
    }

    if (Images.Peel(operand) is IrBinary { Op: IrBinaryOp.ShiftLeft, Left: IrConst { Type.IsInteger: true } constant } shift && constant.Type.Bits == width)
      return constant.Value == 1 ? (Shape.Single, shift.Right) : (Shape.OtherConstant, null);

    return Strip(operand) is IrBinary { Op: IrBinaryOp.ShiftLeft, Left: IrConst { Value: 1 } one } && one.Type.Bits != width
      ? (Shape.OtherWidth, null) : (Shape.None, null);
  }

  /// <summary>The reason a pair of operand shapes is refused, the strongest first; <see langword="null"/> when neither looks like a bit.</summary>
  private static string? Refusal(Shape first, Shape second)
    => first == Shape.OtherWidth || second == Shape.OtherWidth ? "mask computed at another width"
      : first == Shape.OtherConstant || second == Shape.OtherConstant ? "shifted constant is not 1"
      : null;

  /// <summary>The position as the operations take it: a constant or a count masked to the width.</summary>
  private static IrExpr Position(IrExpr position, int width) {
    if (position is IrConst constant)
      return Images.Constant(IrType.I32, constant.Value & (width - 1));

    return position is IrBinary { Op: IrBinaryOp.And, Right: IrConst mask } && mask.Value == width - 1
      ? position
      : new IrBinary(IrBinaryOp.And, position, Images.Constant(IrType.I32, width - 1));
  }

  private static (IrExpr? Candidate, string Form, string? Reason) Test(IrBinary comparison) {
    if (!Images.IsConstant(comparison.Right, out var compared) || compared is not (0 or 1) || Images.Peel(comparison.Left) is not IrBinary { Op: IrBinaryOp.And } and)
      return (null, "", null);

    IrExpr? value = null, position = null;
    var form = "shift-and-mask";
    foreach (var (shifted, mask) in new[] { (and.Left, and.Right), (and.Right, and.Left) })
      if (Images.Peel(shifted) is IrBinary { Op: IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical } shift && Images.IsConstant(mask, out var field)) {
        if (field == 1) {
          (value, position) = (Images.Peel(shift.Left), shift.Right);
          break;
        }

        if (field > 1)
          return (null, "", "field wider than one bit");
      }

    if (value is null) {
      // A mask is compared with zero only: (x & (1 << i)) == 1 holds for no position but 0.
      var (left, right) = (Read(and.Right, and.Type.Bits, false), Read(and.Left, and.Type.Bits, false));
      if (left.Shape == Shape.Single && compared == 0)
        (value, position, form) = (and.Left, left.Position, "mask-and-compare");
      else if (right.Shape == Shape.Single && compared == 0)
        (value, position, form) = (and.Right, right.Position, "mask-and-compare");
      else
        return (null, "", compared == 0 ? Refusal(left.Shape, right.Shape) : null);
    }

    if (!value.Type.IsWideInteger || value.Type.Bits != and.Type.Bits)
      return (null, "", null);

    IrExpr test = Images.Call(IrIntrinsicOp.TestBit, IrType.Bool, value, Position(position!, value.Type.Bits));
    var set = (comparison.Op == IrBinaryOp.NotEqual) == (compared == 0);
    return (set ? test : new IrUnary(IrUnaryOp.LogicalNot, test), form, null);
  }

  private static (IrExpr? Candidate, string Form, string? Reason) Change(IrBinary binary, IrIntrinsicOp op, string form, bool cleared) {
    if (!binary.Type.IsWideInteger)
      return (null, "", null);

    var right = Read(binary.Right, binary.Type.Bits, cleared);
    var left = Read(binary.Left, binary.Type.Bits, cleared);
    var (value, position) = right.Shape == Shape.Single ? (binary.Left, right.Position) : left.Shape == Shape.Single ? (binary.Right, left.Position) : (null, null);
    return value is null || position is null
      ? (null, "", Refusal(left.Shape, right.Shape))
      : (Images.Call(op, binary.Type, value, Position(position, binary.Type.Bits)), form, null);
  }

}
