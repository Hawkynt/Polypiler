using System.Collections.Generic;
using System.Numerics;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>The test for a power of two and the rounding up to one, of every integer type.</summary>
  private static IEnumerable<IntegerOperation> PowersOfTwo => [
    Fixed(IrIntrinsicOp.IsPow2, IrType.Bool, _integers, type => [type], (_, types, arguments) => IsPow2(types[0], Long(arguments, 0))),
    Same(IrIntrinsicOp.RoundUpPow2, _integers, type => [type], (_, types, arguments) => RoundUpPow2(types[0], Long(arguments, 0)))
  ];

  /// <summary>Whether a value is a power of two; never for 0 and never for a negative value.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <returns><see langword="true"/> when exactly one bit is set and the value is positive.</returns>
  public static bool IsPow2(IrType type, long value) => Number(type, value) > 0 && BitOperations.PopCount(Pattern(type, value)) == 1;

  /// <summary>
  /// The smallest power of two that is not below the bit pattern of the value, modulo 2^width: 0
  /// for 0, and 0 where that power is 2^width itself. A signed result is the bit pattern read as
  /// signed, so the power 2^(width-1) of a signed type is its smallest value.
  /// </summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <returns>The power of two, or 0.</returns>
  public static long RoundUpPow2(IrType type, long value) {
    var pattern = Pattern(type, value);
    if (pattern <= 1)
      return value;

    var exponent = 64 - BitOperations.LeadingZeroCount(pattern - 1);
    return exponent >= Width(type) ? 0 : Value(type, 1UL << exponent);
  }

}
