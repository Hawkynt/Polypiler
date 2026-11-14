using System;
using System.Collections.Generic;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>
  /// The divisions that round the quotient down or keep the remainder non-negative, of every integer
  /// type. Truncating division and its remainder are the binary operators of the IR.
  /// </summary>
  private static IEnumerable<IntegerOperation> Division => [
    Divides(IrIntrinsicOp.DivFloor, DivFloor),
    Divides(IrIntrinsicOp.ModFloor, ModFloor),
    Divides(IrIntrinsicOp.DivEuclid, DivEuclid),
    Divides(IrIntrinsicOp.ModEuclid, ModEuclid)
  ];

  private static IntegerOperation Divides(IrIntrinsicOp op, Func<IrType, long, long, long> divide)
    => Same(op, _integers, type => [type, type], (_, types, arguments) => divide(types[0], Long(arguments, 0), Long(arguments, 1)));

  /// <summary>The quotient rounded towards negative infinity.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <returns>The quotient.</returns>
  /// <exception cref="IrTrapException">The divisor is 0, or the smallest value of a signed type is divided by -1.</exception>
  public static long DivFloor(IrType type, long dividend, long divisor) {
    var (a, b) = Divided(type, dividend, divisor);
    var quotient = a / b;
    return (long)(a % b != 0 && (a < 0) != (b < 0) ? quotient - 1 : quotient);
  }

  /// <summary>The remainder of <see cref="DivFloor"/>: it has the sign of the divisor.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <returns>The remainder.</returns>
  /// <exception cref="IrTrapException">Where <see cref="DivFloor"/> traps.</exception>
  public static long ModFloor(IrType type, long dividend, long divisor) {
    var (a, b) = Divided(type, dividend, divisor);
    var remainder = a % b;
    return (long)(remainder != 0 && (remainder < 0) != (b < 0) ? remainder + b : remainder);
  }

  /// <summary>The quotient of the Euclidean division, whose remainder is never negative.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <returns>The quotient.</returns>
  /// <exception cref="IrTrapException">Where <see cref="DivFloor"/> traps.</exception>
  public static long DivEuclid(IrType type, long dividend, long divisor) {
    var (a, b) = Divided(type, dividend, divisor);
    var quotient = a / b;
    return (long)(a % b >= 0 ? quotient : b > 0 ? quotient - 1 : quotient + 1);
  }

  /// <summary>The remainder of the Euclidean division: from 0 to the magnitude of the divisor, exclusive.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="dividend">The dividend.</param>
  /// <param name="divisor">The divisor.</param>
  /// <returns>The remainder.</returns>
  /// <exception cref="IrTrapException">Where <see cref="DivFloor"/> traps.</exception>
  public static long ModEuclid(IrType type, long dividend, long divisor) {
    var (a, b) = Divided(type, dividend, divisor);
    var remainder = a % b;
    return (long)(remainder >= 0 ? remainder : remainder + Int128.Abs(b));
  }

  /// <summary>The two numbers of a division the IR defines; the traps of the division operator otherwise.</summary>
  private static (Int128 Dividend, Int128 Divisor) Divided(IrType type, long dividend, long divisor) {
    var (a, b) = (Number(type, dividend), Number(type, divisor));
    if (b == 0)
      throw new IrTrapException(IrTrap.DivideByZero);

    // Only a signed type has the divisor -1, and its smallest value has no negative.
    if (b == -1 && a == IrOperators.Minimum(type))
      throw new IrTrapException(IrTrap.IntegerOverflow);

    return (a, b);
  }

}
