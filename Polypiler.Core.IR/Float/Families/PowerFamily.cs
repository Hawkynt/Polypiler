using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// The powers: <c>Pow</c> with the special cases of IEEE 754-2019 §9.2.1, converted from CORE-MATH;
/// <c>PowEcma</c>, ECMA-262's exponentiation, which differs from it where the exponent is a NaN and
/// for <c>(±1, ±∞)</c>; and <c>RootN</c>, derived from CORE-MATH's pow with the exponent <c>1/n</c>
/// held in more than double precision. Each <c>ulp(1)</c>, each correctly rounded on every vector.
/// </summary>
internal static class PowerFamily {

  private const string Family = "Power and roots";

  /// <summary>The operations of the family.</summary>
  public static IReadOnlyList<FloatOperation> Operations { get; } = [
    FloatOperation.Binary(IrIntrinsicOp.Pow, Family, FloatContract.Ulp(1), nameof(FloatOperations.Pow), FloatOperations.PowF, FloatOperations.Pow)
      .With(Special.Of(IrIntrinsicOp.Pow, [double.NaN, 0.0], 1.0, "pow(x, ±0) is 1, of a NaN too (§9.2.1)"),
        Special.Of(IrIntrinsicOp.Pow, [1.0, double.NaN], 1.0, "pow(1, y) is 1, of a NaN too (§9.2.1)"),
        Special.Of(IrIntrinsicOp.Pow, [-1.0, double.PositiveInfinity], 1.0, "pow(-1, ±∞) is 1 (§9.2.1)"),
        Special.Of(IrIntrinsicOp.Pow, [Special.NegativeZero, -3.0], double.NegativeInfinity, "pow(-0, odd negative integer) is -∞"),
        Special.Of(IrIntrinsicOp.Pow, [Special.NegativeZero, 3.0], Special.NegativeZero, "pow(-0, odd positive integer) is -0"),
        Special.Of(IrIntrinsicOp.Pow, [Special.NegativeZero, 2.0], 0.0, "pow(-0, even positive integer) is +0"),
        Special.Of(IrIntrinsicOp.Pow, [-2.0, 0.5], double.NaN, "a negative base and a non-integral exponent"),
        Special.Of(IrIntrinsicOp.Pow, [0.5, double.NegativeInfinity], double.PositiveInfinity, "pow(|x| < 1, -∞) is +∞"),
        Special.Of(IrIntrinsicOp.Pow, [2.0, 10.0], 1024.0, "an exact power is exact")),
    FloatOperation.Binary(IrIntrinsicOp.PowEcma, Family, FloatContract.Ulp(1), nameof(FloatOperations.PowEcma), FloatOperations.PowEcmaF, FloatOperations.PowEcma)
      .With(Special.Of(IrIntrinsicOp.PowEcma, [1.0, double.NaN], double.NaN, "ECMA-262: a NaN exponent gives a NaN"),
        Special.Of(IrIntrinsicOp.PowEcma, [-1.0, double.PositiveInfinity], double.NaN, "ECMA-262: (±1, ±∞) gives a NaN"),
        Special.Of(IrIntrinsicOp.PowEcma, [double.NaN, Special.NegativeZero], 1.0, "ECMA-262: an exponent ±0 gives 1")),
    FloatOperation.WithInteger(IrIntrinsicOp.RootN, Family, FloatContract.Ulp(1), nameof(FloatOperations.RootN), FloatOperations.RootNF, FloatOperations.RootN)
      .With(Special.Rooted(-8.0, 3, -2.0, "an odd root keeps the sign"),
        Special.Rooted(-8.0, 2, double.NaN, "an even root of a negative number is a NaN"),
        Special.Rooted(Special.NegativeZero, 2, 0.0, "an even root of -0 is +0"),
        Special.Rooted(Special.NegativeZero, -3, double.NegativeInfinity, "an odd negative root of -0 is -∞"),
        Special.Rooted(4.0, 0, double.NaN, "the 0-th root is a NaN"))
  ];

}
