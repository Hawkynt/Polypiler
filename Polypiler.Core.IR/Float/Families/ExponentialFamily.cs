using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// The exponential functions, each <c>ulp(1)</c> under ADR 0005 and evaluated by a correctly rounded
/// portable implementation converted from CORE-MATH.
/// </summary>
internal static class ExponentialFamily {

  private const string Family = "Exponential";

  /// <summary>The operations of the family.</summary>
  public static IReadOnlyList<FloatOperation> Operations { get; } = [
    FloatOperation.Unary(IrIntrinsicOp.Exp, Family, FloatContract.Ulp(1), nameof(FloatOperations.Exp), FloatOperations.ExpF, FloatOperations.Exp)
      .With(Special.Of(IrIntrinsicOp.Exp, Special.NegativeZero, 1.0, "e^±0 is 1 exactly"),
        Special.Of(IrIntrinsicOp.Exp, double.NegativeInfinity, 0.0, "e^-∞ is +0"),
        Special.Of(IrIntrinsicOp.Exp, 1000.0, double.PositiveInfinity, "an overflow is +∞")),
    FloatOperation.Unary(IrIntrinsicOp.Exp2, Family, FloatContract.Ulp(1), nameof(FloatOperations.Exp2), FloatOperations.Exp2F, FloatOperations.Exp2)
      .With(Special.Of(IrIntrinsicOp.Exp2, 10.0, 1024.0, "an integral exponent gives the exact power"),
        Special.Of(IrIntrinsicOp.Exp2, double.NegativeInfinity, 0.0, "2^-∞ is +0")),
    FloatOperation.Unary(IrIntrinsicOp.Exp10, Family, FloatContract.Ulp(1), nameof(FloatOperations.Exp10), FloatOperations.Exp10F, FloatOperations.Exp10)
      .With(Special.Of(IrIntrinsicOp.Exp10, 3.0, 1000.0, "an exact power of ten is exact"),
        Special.Of(IrIntrinsicOp.Exp10, 0.0, 1.0, "10^0 is 1")),
    FloatOperation.Unary(IrIntrinsicOp.ExpM1, Family, FloatContract.Ulp(1), nameof(FloatOperations.ExpM1), FloatOperations.ExpM1F, FloatOperations.ExpM1)
      .With(Special.Of(IrIntrinsicOp.ExpM1, Special.NegativeZero, Special.NegativeZero, "expm1 keeps the sign of zero"),
        Special.Of(IrIntrinsicOp.ExpM1, double.NegativeInfinity, -1.0, "expm1(-∞) is -1"))
  ];

}
