using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0100: a rotation written as two shifts joined by <c>|</c>, <c>^</c> or <c>+</c> becomes
/// <see cref="IrIntrinsicOp.RotateLeft"/> or <see cref="IrIntrinsicOp.RotateRight"/>.
/// <para>
/// The expression is evaluated as weighted bits of its atoms (<see cref="WeightEvaluator"/>), which
/// follows shifts by constants, masks, conversions and sums without carries exactly. It is a rotation
/// when its value is the bits of one atom, each moved by the same amount modulo the atom's width. A
/// variable count is decided for each of its 64 residues - every shift of the IR uses at most the low
/// six bits of its count - and the rotation is taken only when every residue gives the rotation by
/// that residue (or by its negation). So a right shift that is arithmetic where it matters, amounts
/// that do not add up to the width, two different operands, or <c>^</c> and <c>+</c> with a count that
/// may be zero are left alone. The rotated value has no effect and cannot trap: it is evaluated once
/// instead of twice. A 16-bit rotation by 8 is a byte swap and left to O0106.
/// </para>
/// </summary>
public sealed class O0100Rotate : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0100Rotate() : base("O0100", "rotate from a shift pair") { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    if (!Images.IsCandidate(expression))
      return null;

    var first = IrShape.Nodes(expression).OfType<IrBinary>().FirstOrDefault(node => node.Op is IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical);
    if (first is null)
      return null;

    return first.Right is IrConst ? Constant(expression, context, first.Op == IrBinaryOp.ShiftLeft) : Variable(expression, context, first.Right);
  }

  private static IrExpr? Constant(IrExpr expression, RuleContext context, bool left) {
    if (WeightEvaluator.Evaluate(expression, context) is not { } value || Images.Rotation(value) is not var (atom, count)
      || count == 0 || !IrShape.IsTrapFree(atom) || (atom.Type.Bits == 16 && count == 8))
      return null;

    var width = atom.Type.Bits;
    var rotated = left
      ? Images.Call(IrIntrinsicOp.RotateLeft, atom.Type, atom, Images.Constant(IrType.I32, count))
      : Images.Call(IrIntrinsicOp.RotateRight, atom.Type, atom, Images.Constant(IrType.I32, width - count));
    return Images.Fit(rotated, expression.Type);
  }

  /// <summary>The variable at the core of a count: what remains when constants are added, subtracted, masked off or negated.</summary>
  private static IrExpr Core(IrExpr count) => count switch {
    IrBinary { Right: IrConst } binary when binary.Op is not (IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical) => Core(binary.Left),
    IrBinary { Left: IrConst } binary when binary.Op is not (IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical) => Core(binary.Right),
    IrUnary { Op: IrUnaryOp.Negate or IrUnaryOp.BitNot } unary => Core(unary.Operand),
    IrConvert { Contract: IrConversion.Wrap, Operand.Type.IsWord: true } conversion when conversion.Type.IsWord => Core(conversion.Operand),
    _ => count
  };

  private static IrExpr? Variable(IrExpr expression, RuleContext context, IrExpr count) {
    var variable = Core(count);
    if (variable.Type != IrType.I32 || !IrShape.IsTrapFree(variable))
      return null;

    IrExpr? atom = null;
    var directions = new[] { true, true };
    for (var residue = 0; residue < 64; ++residue) {
      if (WeightEvaluator.Evaluate(expression, context, new CountBinding(variable, residue)) is not { } value
        || Images.Rotation(value) is not var (rotated, amount) || (atom is not null && !IrShape.Same(atom, rotated)))
        return null;

      atom = rotated;
      var width = rotated.Type.Bits;
      directions[0] &= amount == residue % width;
      directions[1] &= amount == (width - residue % width) % width;
    }

    if (!IrShape.IsTrapFree(atom!))
      return null;

    var op = directions[0] ? IrIntrinsicOp.RotateLeft : directions[1] ? IrIntrinsicOp.RotateRight : (IrIntrinsicOp?)null;
    return op is { } direction ? Images.Fit(Images.Call(direction, atom!.Type, atom, variable), expression.Type) : null;
  }

}
