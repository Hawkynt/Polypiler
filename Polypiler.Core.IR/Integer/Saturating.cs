using System;
using System.Collections.Generic;
using System.Numerics;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>The arithmetic that clamps its exact result to the range of the type, of every integer type.</summary>
  private static IEnumerable<IntegerOperation> Saturating => [
    Same(IrIntrinsicOp.AddSaturate, _integers, type => [type, type], (_, types, arguments) => AddSaturate(types[0], Long(arguments, 0), Long(arguments, 1))),
    Same(IrIntrinsicOp.SubSaturate, _integers, type => [type, type], (_, types, arguments) => SubSaturate(types[0], Long(arguments, 0), Long(arguments, 1))),
    Same(IrIntrinsicOp.MulSaturate, _integers, type => [type, type], (_, types, arguments) => MulSaturate(types[0], Long(arguments, 0), Long(arguments, 1)))
  ];

  /// <summary>The sum, or the nearest value of the type when the sum lies outside.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  /// <returns>The saturated sum.</returns>
  public static long AddSaturate(IrType type, long left, long right) => Saturate(type, Number(type, left) + Number(type, right));

  /// <summary>The difference, or the nearest value of the type when the difference lies outside.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  /// <returns>The saturated difference.</returns>
  public static long SubSaturate(IrType type, long left, long right) => Saturate(type, Number(type, left) - Number(type, right));

  /// <summary>The product, or the nearest value of the type when the product lies outside.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  /// <returns>The saturated product.</returns>
  public static long MulSaturate(IrType type, long left, long right) {
    // The product of two u64 can reach 2^128, which no Int128 holds.
    var product = (BigInteger)Number(type, left) * (BigInteger)Number(type, right);
    return Saturate(type, (Int128)BigInteger.Clamp(product, (BigInteger)IrOperators.Minimum(type), (BigInteger)IrOperators.Maximum(type)));
  }

}
