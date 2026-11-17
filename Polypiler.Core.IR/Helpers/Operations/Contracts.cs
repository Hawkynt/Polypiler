// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The integer operations whose contract no operator of the IR has (#118): a division or remainder
/// that gives 0 for a zero divisor (<c>[divzero=zero]</c>) or wraps <c>MIN / -1</c> (<c>ovf=wrap</c>), and a
/// shift whose amount is not below the width shifts every bit out (<c>[unbounded]</c>) or raises a
/// trap (<c>[trap &lt;label&gt;]</c>, <c>[strict]</c>). A type narrower than <c>int</c> is computed as an
/// <c>int</c>, its smallest value or width given. A trap is named by the number of the IR's trap:
/// 0 a division by zero, 2 an integer overflow, any other non-negative number an invalid operation,
/// a negative one none.
/// </summary>
public static partial class HelperOperations {

  /// <summary>The quotient of two <c>int</c> truncated towards zero, under a zero-divisor and an <c>ovf</c> contract.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <param name="smallest">The smallest value of the type divided.</param>
  /// <param name="zero">Whether a zero divisor gives 0 rather than trapping.</param>
  /// <param name="wrap">Whether the smallest value divided by -1 gives the smallest value rather than trapping.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0 and <paramref name="zero"/> is not set.</exception>
  /// <exception cref="OverflowException">The smallest value is divided by -1 and <paramref name="wrap"/> is not set.</exception>
  public static int DivideContract32(int dividend, int divisor, int smallest, bool zero, bool wrap) {
    if (divisor == 0) {
      if (zero)
        return 0;

      throw new DivideByZeroException("divide by zero");
    }

    if (dividend == smallest && divisor == -1) {
      if (wrap)
        return smallest;

      throw new OverflowException("integer overflow");
    }

    return dividend / divisor;
  }

  /// <summary>The remainder of <see cref="DivideContract32"/>, with the sign of the dividend; 0 for the smallest value and -1 under <paramref name="wrap"/>.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <param name="smallest">The smallest value of the type divided.</param>
  /// <param name="zero">Whether a zero divisor gives 0 rather than trapping.</param>
  /// <param name="wrap">Whether the smallest value divided by -1 gives 0 rather than trapping.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0 and <paramref name="zero"/> is not set.</exception>
  /// <exception cref="OverflowException">The smallest value is divided by -1 and <paramref name="wrap"/> is not set.</exception>
  public static int RemainderContract32(int dividend, int divisor, int smallest, bool zero, bool wrap) {
    if (divisor == 0) {
      if (zero)
        return 0;

      throw new DivideByZeroException("divide by zero");
    }

    if (dividend == smallest && divisor == -1) {
      if (wrap)
        return 0;

      throw new OverflowException("integer overflow");
    }

    return dividend % divisor;
  }

  /// <summary>The quotient of two <c>long</c> truncated towards zero, under a zero-divisor and an <c>ovf</c> contract.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <param name="zero">Whether a zero divisor gives 0 rather than trapping.</param>
  /// <param name="wrap">Whether <c>long.MinValue / -1</c> gives <c>long.MinValue</c> rather than trapping.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0 and <paramref name="zero"/> is not set.</exception>
  /// <exception cref="OverflowException"><c>long.MinValue</c> is divided by -1 and <paramref name="wrap"/> is not set.</exception>
  public static long DivideContract64(long dividend, long divisor, bool zero, bool wrap) {
    if (divisor == 0L) {
      if (zero)
        return 0L;

      throw new DivideByZeroException("divide by zero");
    }

    if (dividend == long.MinValue && divisor == -1L) {
      if (wrap)
        return long.MinValue;

      throw new OverflowException("integer overflow");
    }

    return dividend / divisor;
  }

  /// <summary>The remainder of <see cref="DivideContract64"/>, with the sign of the dividend.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <param name="zero">Whether a zero divisor gives 0 rather than trapping.</param>
  /// <param name="wrap">Whether <c>long.MinValue % -1</c> gives 0 rather than trapping.</param>
  /// <exception cref="DivideByZeroException">The divisor is 0 and <paramref name="zero"/> is not set.</exception>
  /// <exception cref="OverflowException"><c>long.MinValue</c> is divided by -1 and <paramref name="wrap"/> is not set.</exception>
  public static long RemainderContract64(long dividend, long divisor, bool zero, bool wrap) {
    if (divisor == 0L) {
      if (zero)
        return 0L;

      throw new DivideByZeroException("divide by zero");
    }

    if (dividend == long.MinValue && divisor == -1L) {
      if (wrap)
        return 0L;

      throw new OverflowException("integer overflow");
    }

    return dividend % divisor;
  }

  /// <summary>The quotient of two <c>uint</c>, 0 for a zero divisor.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  public static uint DivideOrZeroUnsigned32(uint dividend, uint divisor) => divisor == 0u ? 0u : dividend / divisor;

  /// <summary>The remainder of two <c>uint</c>, 0 for a zero divisor.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  public static uint RemainderOrZeroUnsigned32(uint dividend, uint divisor) => divisor == 0u ? 0u : dividend % divisor;

  /// <summary>The quotient of two <c>ulong</c>, 0 for a zero divisor.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  public static ulong DivideOrZeroUnsigned64(ulong dividend, ulong divisor) => divisor == 0UL ? 0UL : dividend / divisor;

  /// <summary>The remainder of two <c>ulong</c>, 0 for a zero divisor.</summary>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  public static ulong RemainderOrZeroUnsigned64(ulong dividend, ulong divisor) => divisor == 0UL ? 0UL : dividend % divisor;

  /// <summary>A value of up to 32 bits shifted left; an amount not below the width raises the trap or gives 0.</summary>
  /// <param name="value">The value.</param>
  /// <param name="amount">The amount, read as an unsigned number.</param>
  /// <param name="width">The width of the type, at most 32.</param>
  /// <param name="trap">The trap an amount not below the width raises; negative for none.</param>
  /// <exception cref="ArithmeticException">The amount is not below the width and <paramref name="trap"/> names a trap.</exception>
  public static int ShiftLeftBounded32(int value, uint amount, int width, int trap) {
    if (amount >= (uint)width) {
      RaiseShiftTrap(trap);
      return 0;
    }

    return value << (int)amount;
  }

  /// <summary>The bits of a value of up to 32 bits shifted right, zeros shifted in; an amount not below the width raises the trap or gives 0.</summary>
  /// <param name="value">The value, its bits zero-extended.</param>
  /// <param name="amount">The amount, read as an unsigned number.</param>
  /// <param name="width">The width of the type, at most 32.</param>
  /// <param name="trap">The trap an amount not below the width raises; negative for none.</param>
  /// <exception cref="ArithmeticException">The amount is not below the width and <paramref name="trap"/> names a trap.</exception>
  public static uint ShiftRightLogicalBounded32(uint value, uint amount, int width, int trap) {
    if (amount >= (uint)width) {
      RaiseShiftTrap(trap);
      return 0u;
    }

    return value >> (int)amount;
  }

  /// <summary>A value of up to 32 bits shifted right, its sign replicated; an amount not below the width raises the trap or gives the sign fill.</summary>
  /// <param name="value">The value, sign-extended.</param>
  /// <param name="amount">The amount, read as an unsigned number.</param>
  /// <param name="width">The width of the type, at most 32.</param>
  /// <param name="trap">The trap an amount not below the width raises; negative for none.</param>
  /// <exception cref="ArithmeticException">The amount is not below the width and <paramref name="trap"/> names a trap.</exception>
  public static int ShiftRightArithmeticBounded32(int value, uint amount, int width, int trap) {
    if (amount >= (uint)width) {
      RaiseShiftTrap(trap);
      return value < 0 ? -1 : 0;
    }

    return value >> (int)amount;
  }

  /// <summary>A <c>long</c> shifted left; an amount not below 64 raises the trap or gives 0.</summary>
  /// <param name="value">The value.</param>
  /// <param name="amount">The amount, read as an unsigned number.</param>
  /// <param name="trap">The trap an amount not below 64 raises; negative for none.</param>
  /// <exception cref="ArithmeticException">The amount is not below 64 and <paramref name="trap"/> names a trap.</exception>
  public static long ShiftLeftBounded64(long value, ulong amount, int trap) {
    if (amount >= 64UL) {
      RaiseShiftTrap(trap);
      return 0L;
    }

    return value << (int)amount;
  }

  /// <summary>A <c>ulong</c> shifted right, zeros shifted in; an amount not below 64 raises the trap or gives 0.</summary>
  /// <param name="value">The value.</param>
  /// <param name="amount">The amount, read as an unsigned number.</param>
  /// <param name="trap">The trap an amount not below 64 raises; negative for none.</param>
  /// <exception cref="ArithmeticException">The amount is not below 64 and <paramref name="trap"/> names a trap.</exception>
  public static ulong ShiftRightLogicalBounded64(ulong value, ulong amount, int trap) {
    if (amount >= 64UL) {
      RaiseShiftTrap(trap);
      return 0UL;
    }

    return value >> (int)amount;
  }

  /// <summary>A <c>long</c> shifted right, its sign replicated; an amount not below 64 raises the trap or gives the sign fill.</summary>
  /// <param name="value">The value.</param>
  /// <param name="amount">The amount, read as an unsigned number.</param>
  /// <param name="trap">The trap an amount not below 64 raises; negative for none.</param>
  /// <exception cref="ArithmeticException">The amount is not below 64 and <paramref name="trap"/> names a trap.</exception>
  public static long ShiftRightArithmeticBounded64(long value, ulong amount, int trap) {
    if (amount >= 64UL) {
      RaiseShiftTrap(trap);
      return value < 0L ? -1L : 0L;
    }

    return value >> (int)amount;
  }

  /// <summary>Raises the trap a number names: 0 a division by zero, 2 an integer overflow, another non-negative number an invalid operation, a negative one none.</summary>
  /// <param name="trap">The number of the trap.</param>
  /// <exception cref="DivideByZeroException"><paramref name="trap"/> is 0.</exception>
  /// <exception cref="OverflowException"><paramref name="trap"/> is 2.</exception>
  /// <exception cref="ArithmeticException"><paramref name="trap"/> is another non-negative number.</exception>
  public static void RaiseShiftTrap(int trap) {
    if (trap == 0)
      throw new DivideByZeroException("divide by zero");

    if (trap == 2)
      throw new OverflowException("integer overflow");

    if (trap >= 0)
      throw new ArithmeticException("invalid operation");
  }

}
