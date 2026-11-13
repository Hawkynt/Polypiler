using System;

namespace Polypiler.Core.IR;

/// <summary>The typing rules of the operators, shared by everything that builds or checks IR.</summary>
public static class IrOperators {

  /// <summary>Whether a unary operator is defined on an operand type.</summary>
  /// <param name="op">The operator.</param>
  /// <param name="operand">The type of the operand.</param>
  /// <returns><see langword="true"/> when the operator applies.</returns>
  public static bool Accepts(IrUnaryOp op, IrType operand) => op switch {
    IrUnaryOp.Negate => operand.IsArithmetic && operand != IrType.U32 && operand != IrType.U64,
    IrUnaryOp.NegateChecked => operand == IrType.I32 || operand == IrType.I64,
    IrUnaryOp.BitNot => operand.IsWideInteger,
    _ => operand == IrType.Bool
  };

  /// <summary>Whether a binary operator is defined on a pair of operand types.</summary>
  /// <param name="op">The operator.</param>
  /// <param name="left">The type of the left operand.</param>
  /// <param name="right">The type of the right operand.</param>
  /// <returns><see langword="true"/> when the operator applies.</returns>
  public static bool Accepts(IrBinaryOp op, IrType left, IrType right) => op switch {
    IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr => left == IrType.Bool && right == IrType.Bool,
    IrBinaryOp.Equal or IrBinaryOp.NotEqual => (left.IsArithmetic || left == IrType.Bool) && right == left,
    IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical => left.IsWideInteger && right == IrType.I32,
    IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked
      or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor => left.IsWideInteger && right == left,
    _ => left.IsArithmetic && right == left
  };

  /// <summary>Whether an operator traps instead of wrapping around when its result does not fit.</summary>
  /// <param name="op">The operator.</param>
  /// <returns><see langword="true"/> for the checked forms of addition, subtraction and multiplication.</returns>
  public static bool Traps(IrBinaryOp op)
    => op is IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked;

  /// <summary>
  /// Reduces a number to the value an integer type holds for it: the number modulo 2^width. A value of
  /// <c>u64</c> is carried as the 64 bits it consists of, so it is unchanged like one of <c>i64</c>.
  /// </summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">Any number.</param>
  /// <returns>The value within the range of <paramref name="type"/>.</returns>
  public static long Wrap(IrType type, long value) => type.Kind switch {
    IrTypeKind.I8 => (sbyte)value,
    IrTypeKind.U8 => (byte)value,
    IrTypeKind.I16 => (short)value,
    IrTypeKind.U16 => (ushort)value,
    IrTypeKind.I32 => (int)value,
    IrTypeKind.U32 => (uint)value,
    _ => value
  };

  /// <summary>The smallest value of an integer type.</summary>
  /// <param name="type">An integer type.</param>
  /// <returns>The minimum, 0 for an unsigned type.</returns>
  public static Int128 Minimum(IrType type) => type.IsSigned ? -(Int128.One << (type.Bits - 1)) : Int128.Zero;

  /// <summary>The largest value of an integer type.</summary>
  /// <param name="type">An integer type.</param>
  /// <returns>The maximum.</returns>
  public static Int128 Maximum(IrType type) => (Int128.One << (type.IsSigned ? type.Bits - 1 : type.Bits)) - 1;

  /// <summary>The number a carried integer stands for: the 64 bits of a <c>u64</c> read as unsigned.</summary>
  /// <param name="type">The integer type of the value.</param>
  /// <param name="value">The value as the IR carries it.</param>
  /// <returns>The mathematical value.</returns>
  public static Int128 Number(IrType type, long value) => type == IrType.U64 ? (ulong)value : value;

  /// <summary>Whether every value of one integer type is also a value of another.</summary>
  /// <param name="type">The type that is to hold the values.</param>
  /// <param name="other">The type the values come from.</param>
  /// <returns><see langword="true"/> when a conversion from <paramref name="other"/> loses nothing.</returns>
  public static bool Contains(IrType type, IrType other)
    => Minimum(type) <= Minimum(other) && Maximum(type) >= Maximum(other);

}
