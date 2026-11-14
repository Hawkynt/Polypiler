using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// The inverse trigonometric functions, each <c>ulp(1)</c> under ADR 0005 and evaluated by a correctly
/// rounded portable implementation converted from CORE-MATH. <c>Atan2(y, x)</c> takes the ordinate first.
/// </summary>
internal static class InverseTrigonometricFamily {

  private const string Family = "Inverse trigonometric";
  private const double HalfPi = 1.5707963267948966;
  private const double Pi = 3.141592653589793;

  /// <summary>The operations of the family.</summary>
  public static IReadOnlyList<FloatOperation> Operations { get; } = [
    FloatOperation.Unary(IrIntrinsicOp.Asin, Family, FloatContract.Ulp(1), nameof(FloatOperations.Asin), FloatOperations.AsinF, FloatOperations.Asin)
      .With(Special.Of(IrIntrinsicOp.Asin, 1.0, HalfPi, "asin(1) is π/2 rounded"),
        Special.Of(IrIntrinsicOp.Asin, -1.0, -HalfPi, "asin(-1) is -π/2 rounded"),
        Special.Of(IrIntrinsicOp.Asin, Special.NegativeZero, Special.NegativeZero, "asin keeps the sign of zero"),
        Special.Of(IrIntrinsicOp.Asin, 1.5, double.NaN, "outside [-1, 1] there is no arcsine")),
    FloatOperation.Unary(IrIntrinsicOp.Acos, Family, FloatContract.Ulp(1), nameof(FloatOperations.Acos), FloatOperations.AcosF, FloatOperations.Acos)
      .With(Special.Of(IrIntrinsicOp.Acos, 1.0, 0.0, "acos(1) is +0"),
        Special.Of(IrIntrinsicOp.Acos, -1.0, Pi, "acos(-1) is π rounded"),
        Special.Of(IrIntrinsicOp.Acos, -1.5, double.NaN, "outside [-1, 1] there is no arccosine")),
    FloatOperation.Unary(IrIntrinsicOp.Atan, Family, FloatContract.Ulp(1), nameof(FloatOperations.Atan), FloatOperations.AtanF, FloatOperations.Atan)
      .With(Special.Of(IrIntrinsicOp.Atan, double.PositiveInfinity, HalfPi, "atan(+∞) is π/2 rounded"),
        Special.Of(IrIntrinsicOp.Atan, Special.NegativeZero, Special.NegativeZero, "atan keeps the sign of zero")),
    FloatOperation.Binary(IrIntrinsicOp.Atan2, Family, FloatContract.Ulp(1), nameof(FloatOperations.Atan2), FloatOperations.Atan2F, FloatOperations.Atan2)
      .With(Special.Of(IrIntrinsicOp.Atan2, [Special.NegativeZero, -1.0], -Pi, "atan2(-0, x < 0) is -π rounded"),
        Special.Of(IrIntrinsicOp.Atan2, [0.0, -1.0], Pi, "atan2(+0, x < 0) is π rounded"),
        Special.Of(IrIntrinsicOp.Atan2, [Special.NegativeZero, 1.0], Special.NegativeZero, "atan2(-0, x > 0) is -0"),
        Special.Of(IrIntrinsicOp.Atan2, [0.0, 1.0], 0.0, "atan2(+0, x > 0) is +0"),
        Special.Of(IrIntrinsicOp.Atan2, [Special.NegativeZero, Special.NegativeZero], -Pi, "atan2(-0, -0) is -π rounded"),
        Special.Of(IrIntrinsicOp.Atan2, [0.0, Special.NegativeZero], Pi, "atan2(+0, -0) is π rounded"),
        Special.Of(IrIntrinsicOp.Atan2, [Special.NegativeZero, 0.0], Special.NegativeZero, "atan2(-0, +0) is -0"),
        Special.Of(IrIntrinsicOp.Atan2, [0.0, 0.0], 0.0, "atan2(+0, +0) is +0"),
        Special.Of(IrIntrinsicOp.Atan2, [1.0, 0.0], HalfPi, "atan2(y > 0, ±0) is π/2 rounded"),
        Special.Of(IrIntrinsicOp.Atan2, [-1.0, Special.NegativeZero], -HalfPi, "atan2(y < 0, ±0) is -π/2 rounded"),
        Special.Of(IrIntrinsicOp.Atan2, [1.0, double.NegativeInfinity], Pi, "atan2(y > 0, -∞) is π rounded"),
        Special.Of(IrIntrinsicOp.Atan2, [-1.0, double.PositiveInfinity], Special.NegativeZero, "atan2(y < 0, +∞) is -0"),
        Special.Of(IrIntrinsicOp.Atan2, [double.PositiveInfinity, double.PositiveInfinity], Pi / 4, "atan2(+∞, +∞) is π/4 rounded"),
        Special.Of(IrIntrinsicOp.Atan2, [double.NegativeInfinity, double.NegativeInfinity], -3 * Pi / 4, "atan2(-∞, -∞) is -3π/4 rounded"),
        Special.Of(IrIntrinsicOp.Atan2, [double.PositiveInfinity, 1.0], HalfPi, "atan2(+∞, finite x) is π/2 rounded"),
        Special.Of(IrIntrinsicOp.Atan2, [double.NaN, 1.0], double.NaN, "a NaN operand gives a NaN"))
  ];

}
