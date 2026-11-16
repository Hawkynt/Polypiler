using System;
using System.Numerics;

namespace Polypiler.Core.IR;

/// <summary>A constant of the arbitrary-precision integer type <see cref="IrType.BigInt"/>, of any magnitude.</summary>
/// <param name="Value">The integer.</param>
public sealed record IrBigConst(BigInteger Value) : IrExpr(IrType.BigInt);

/// <summary>
/// The rules of the arbitrary-precision integer <see cref="IrType.BigInt"/>: an exact integer of any
/// magnitude, as Python's <c>int</c>, .NET's <see cref="BigInteger"/> and ECMAScript's <c>BigInt</c>
/// are. Addition, subtraction, multiplication and negation are exact and never trap. Division and
/// remainder truncate towards zero, as the IR's operators do on every other type, and trap on a zero
/// divisor only; the frontend of a language with floor division builds it from them. The bitwise
/// operators work on the infinite two's complement of their operands, so <c>-1 &amp; 0xFF</c> is 255
/// and the complement of <c>x</c> is <c>-x - 1</c>. A shift takes an <c>i32</c> count that is never
/// masked: <c>x &lt;&lt; n</c> is <c>x * 2^n</c>, <c>x &gt;&gt; n</c> the floor of <c>x / 2^n</c>, and a
/// negative count shifts the other way, as .NET and ECMAScript do. Comparisons order the integers.
/// </summary>
/// <remarks>
/// Conversions: to a fixed-width integer with <see cref="IrConversion.Wrap"/> the value modulo
/// 2^width, with <see cref="IrConversion.Trap"/> the overflow trap outside the type; from one, exact.
/// To <c>f64</c> (and to no other floating-point type) the nearest number, ties to even, an infinity beyond the largest finite one;
/// from floating point with <see cref="IrConversion.Trap"/> only: truncated towards zero, a NaN or an
/// infinity traps with an integer overflow.
/// </remarks>
public static class IrBigInts {

  /// <summary>Whether a unary operator is defined on the arbitrary-precision integer: negation and the complement.</summary>
  /// <param name="op">The operator.</param>
  /// <returns><see langword="true"/> when it applies.</returns>
  public static bool Accepts(IrUnaryOp op) => op is IrUnaryOp.Negate or IrUnaryOp.BitNot;

  /// <summary>Whether a binary operator is defined on an arbitrary-precision left operand and a right operand.</summary>
  /// <param name="op">The operator.</param>
  /// <param name="right">The type of the right operand: the shift count an <c>i32</c>, every other operand an arbitrary-precision integer.</param>
  /// <returns><see langword="true"/> when it applies.</returns>
  public static bool Accepts(IrBinaryOp op, IrType right) => op switch {
    IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight => right == IrType.I32,
    IrBinaryOp.ShiftRightLogical or IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked
      or IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr => false,
    _ => right == IrType.BigInt
  };

  /// <summary>Whether a conversion from or to the arbitrary-precision integer is defined with a contract.</summary>
  /// <param name="from">The type converted from.</param>
  /// <param name="to">The type converted to.</param>
  /// <param name="contract">What happens to a value the target cannot hold.</param>
  /// <returns><see langword="true"/> when the conversion applies.</returns>
  public static bool Converts(IrType from, IrType to, IrConversion contract) {
    var other = from.IsBigInt ? to : from;
    if (other.IsBigInt || other.IsEnum || !other.IsNumeric)
      return false;

    // To floating point only binary64: a rounding to binary32 through it would round twice.
    if (other.IsFloat)
      return from.IsBigInt ? to == IrType.F64 && contract == IrConversion.Wrap : contract == IrConversion.Trap;

    return contract is IrConversion.Wrap or IrConversion.Trap;
  }

  /// <summary>An operator on one arbitrary-precision integer.</summary>
  /// <param name="op">Negation or the complement.</param>
  /// <param name="value">The operand.</param>
  /// <returns>The result.</returns>
  public static BigInteger Evaluate(IrUnaryOp op, BigInteger value) => op == IrUnaryOp.BitNot ? -value - 1 : -value;

  /// <summary>An operator on two arbitrary-precision integers, or on one and a shift count.</summary>
  /// <param name="op">The operator.</param>
  /// <param name="left">The left operand.</param>
  /// <param name="right">The right operand; a shift count within the range of <c>i32</c>.</param>
  /// <returns>The integer, or for a comparison the truth value.</returns>
  /// <exception cref="IrTrapException">A division or a remainder by zero.</exception>
  public static object Evaluate(IrBinaryOp op, BigInteger left, BigInteger right) {
    switch (op) {
      case IrBinaryOp.Divide or IrBinaryOp.Remainder when right.IsZero:
        throw new IrTrapException(IrTrap.DivideByZero);
      case IrBinaryOp.ShiftLeft:
        return Shift(left, (long)right);
      case IrBinaryOp.ShiftRight:
        return Shift(left, -(long)right);
    }

    return op switch {
      IrBinaryOp.Add => left + right,
      IrBinaryOp.Subtract => left - right,
      IrBinaryOp.Multiply => left * right,
      IrBinaryOp.Divide => BigInteger.Divide(left, right),
      IrBinaryOp.Remainder => BigInteger.Remainder(left, right),
      IrBinaryOp.And => left & right,
      IrBinaryOp.Or => left | right,
      IrBinaryOp.Xor => left ^ right,
      IrBinaryOp.Equal => left == right,
      IrBinaryOp.NotEqual => left != right,
      IrBinaryOp.Less => left < right,
      IrBinaryOp.LessOrEqual => left <= right,
      IrBinaryOp.Greater => left > right,
      _ => (object)(left >= right)
    };
  }

  /// <summary>The integer times 2^count, rounded towards negative infinity for a negative count.</summary>
  /// <param name="value">The integer.</param>
  /// <param name="count">The power of two; negative to divide.</param>
  /// <returns>The shifted integer.</returns>
  public static BigInteger Shift(BigInteger value, long count) {
    if (count >= 0)
      return value << (int)count;

    // A right shift as far as the magnitude reaches leaves the sign alone.
    if (-count > value.GetBitLength())
      return value.Sign < 0 ? BigInteger.MinusOne : BigInteger.Zero;

    return value >> (int)-count;
  }

  /// <summary>The integer modulo 2^width, as a value of a fixed-width type carried by the IR.</summary>
  /// <param name="type">A fixed-width integer type.</param>
  /// <param name="value">The integer.</param>
  /// <returns>The value of the type that is congruent to the integer modulo 2^width.</returns>
  public static long Wrap(IrType type, BigInteger value) => IrOperators.Wrap(type, unchecked((long)(ulong)(value & ulong.MaxValue)));

  /// <summary>The integer as a value of a fixed-width type, or the overflow trap when it is not one.</summary>
  /// <param name="type">A fixed-width integer type.</param>
  /// <param name="value">The integer.</param>
  /// <returns>The value as the IR carries it.</returns>
  /// <exception cref="IrTrapException">The integer lies outside the type.</exception>
  public static long Fit(IrType type, BigInteger value) {
    if (value < (BigInteger)IrOperators.Minimum(type) || value > (BigInteger)IrOperators.Maximum(type))
      throw new IrTrapException(IrTrap.IntegerOverflow);

    return Wrap(type, value);
  }

  /// <summary>The integer a value of a fixed-width type stands for.</summary>
  /// <param name="type">A fixed-width integer type.</param>
  /// <param name="value">The value as the IR carries it.</param>
  /// <returns>The integer.</returns>
  public static BigInteger Of(IrType type, long value) => (BigInteger)IrOperators.Number(type, value);

  /// <summary>
  /// The nearest binary64 number, ties to even, and an infinity of the sign beyond the largest finite
  /// one. The magnitude is cut to 55 bits whose lowest one is sticky - set when any bit cut off was -
  /// so that the one rounding of the conversion to binary64 is the correct one; the scaling by a power
  /// of two after it is exact or overflows.
  /// </summary>
  /// <param name="value">The integer.</param>
  /// <returns>The number.</returns>
  public static double ToDouble(BigInteger value) {
    var (kept, cut) = Cut(value, 55);
    return Math.ScaleB((double)kept, cut);
  }

  /// <summary>The integer as a signed number of at most a given count of bits, the bits cut off folded into the lowest as a sticky bit, and the power of two it is scaled by.</summary>
  private static (long Kept, int Cut) Cut(BigInteger value, int width) {
    var magnitude = BigInteger.Abs(value);
    var cut = Math.Max(0, (int)magnitude.GetBitLength() - width);
    var kept = (long)(magnitude >> cut);
    if (cut > 0 && !(magnitude & ((BigInteger.One << cut) - 1)).IsZero)
      kept |= 1;

    return (value.Sign < 0 ? -kept : kept, cut);
  }

  /// <summary>A floating-point number truncated towards zero, exactly; the overflow trap for a NaN and an infinity.</summary>
  /// <param name="number">The number.</param>
  /// <returns>The integer.</returns>
  /// <exception cref="IrTrapException">The number is a NaN or an infinity.</exception>
  public static BigInteger FromDouble(double number)
    => double.IsFinite(number) ? new BigInteger(Math.Truncate(number)) : throw new IrTrapException(IrTrap.IntegerOverflow);

}
