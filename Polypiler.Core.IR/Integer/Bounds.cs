using System;
using System.Collections.Generic;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>
  /// The smaller and the larger of two values, a value within bounds, the magnitude and the sign, of
  /// every integer type. The smaller, the larger, the value within bounds, the magnitude and the sign
  /// are defined on floating-point numbers as well, which is not this class's concern.
  /// </summary>
  private static IEnumerable<IntegerOperation> Bounds => [
    Same(IrIntrinsicOp.Min, _integers, type => [type, type], (_, types, arguments) => Min(types[0], Long(arguments, 0), Long(arguments, 1)), true),
    Same(IrIntrinsicOp.Max, _integers, type => [type, type], (_, types, arguments) => Max(types[0], Long(arguments, 0), Long(arguments, 1)), true),
    Same(IrIntrinsicOp.Clamp, _integers, type => [type, type, type],
      (_, types, arguments) => Clamp(types[0], Long(arguments, 0), Long(arguments, 1), Long(arguments, 2)), true),
    Same(IrIntrinsicOp.Abs, _integers, type => [type], (_, types, arguments) => Abs(types[0], Long(arguments, 0)), true),
    Same(IrIntrinsicOp.AbsWrap, _integers, type => [type], (_, types, arguments) => AbsWrap(types[0], Long(arguments, 0))),
    Fixed(IrIntrinsicOp.Sign, IrType.I32, _integers, type => [type], (_, types, arguments) => (long)Sign(types[0], Long(arguments, 0)), true)
  ];

  /// <summary>The smaller of two values.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  /// <returns>The smaller one.</returns>
  public static long Min(IrType type, long left, long right) => Number(type, left) <= Number(type, right) ? left : right;

  /// <summary>The larger of two values.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  /// <returns>The larger one.</returns>
  public static long Max(IrType type, long left, long right) => Number(type, left) >= Number(type, right) ? left : right;

  /// <summary>The value, or the nearer bound when it lies outside them.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <param name="low">The smallest result.</param>
  /// <param name="high">The largest result.</param>
  /// <returns>The value within the bounds.</returns>
  /// <exception cref="IrTrapException">The low bound lies above the high one: an invalid operation.</exception>
  public static long Clamp(IrType type, long value, long low, long high) {
    Require(Number(type, low) <= Number(type, high));
    return Min(type, Max(type, value, low), high);
  }

  /// <summary>The magnitude of a value; an unsigned value is its own.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <returns>The magnitude.</returns>
  /// <exception cref="IrTrapException">The smallest value of a signed type, whose magnitude the type does not hold: an integer overflow.</exception>
  public static long Abs(IrType type, long value) => Fit(type, Int128.Abs(Number(type, value)));

  /// <summary>The magnitude of a value modulo 2^width: the smallest value of a signed type is its own.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <returns>The magnitude, wrapped around.</returns>
  public static long AbsWrap(IrType type, long value) => Number(type, value) < 0 ? IrOperators.Wrap(type, -value) : value;

  /// <summary>-1, 0 or 1 for a negative, zero or positive value.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <returns>The sign.</returns>
  public static int Sign(IrType type, long value) => Int128.Sign(Number(type, value));

}
