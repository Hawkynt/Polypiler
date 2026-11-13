namespace Polypiler.Core.IR;

/// <summary>The typing rules of the operators, shared by everything that builds or checks IR.</summary>
public static class IrOperators {

  /// <summary>Whether a unary operator is defined on an operand type.</summary>
  /// <param name="op">The operator.</param>
  /// <param name="operand">The type of the operand.</param>
  /// <returns><see langword="true"/> when the operator applies.</returns>
  public static bool Accepts(IrUnaryOp op, IrType operand) => op switch {
    IrUnaryOp.Negate => operand == IrType.I32,
    IrUnaryOp.BitNot => operand.IsWord,
    _ => operand == IrType.Bool
  };

  /// <summary>Whether a binary operator is defined on a pair of operand types.</summary>
  /// <param name="op">The operator.</param>
  /// <param name="left">The type of the left operand.</param>
  /// <param name="right">The type of the right operand.</param>
  /// <returns><see langword="true"/> when the operator applies.</returns>
  public static bool Accepts(IrBinaryOp op, IrType left, IrType right) => op switch {
    IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr => left == IrType.Bool && right == IrType.Bool,
    IrBinaryOp.Equal or IrBinaryOp.NotEqual => (left.IsWord || left == IrType.Bool) && right == left,
    IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical => left.IsWord && right == IrType.I32,
    _ => left.IsWord && right == left
  };

  /// <summary>Reduces a number to the value an integer type holds for it: the number modulo 2^width.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">Any number.</param>
  /// <returns>The value within the range of <paramref name="type"/>.</returns>
  public static long Wrap(IrType type, long value) => type.Kind switch {
    IrTypeKind.I32 => (int)value,
    IrTypeKind.U32 => (uint)value,
    _ => (byte)value
  };

}
