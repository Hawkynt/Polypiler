using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// The roots: cube root, the length of a vector and the reciprocal square root, each correctly
/// rounded by its portable implementation (converted from CORE-MATH) under the contract <c>ulp(1)</c>
/// of ADR 0005.
/// </summary>
internal static class RootsFamily {

  private const string Family = "Power and roots";

  /// <summary>The operations of the family.</summary>
  public static IReadOnlyList<FloatOperation> Operations { get; } = [
    FloatOperation.Unary(IrIntrinsicOp.Cbrt, Family, FloatContract.Ulp(1), nameof(FloatOperations.Cbrt), FloatOperations.CbrtF, FloatOperations.Cbrt)
      .With(Special.Of(IrIntrinsicOp.Cbrt, Special.NegativeZero, Special.NegativeZero, "the cube root keeps the sign of zero"),
        Special.Of(IrIntrinsicOp.Cbrt, -27.0, -3.0, "an exact cube root is exact"),
        Special.Of(IrIntrinsicOp.Cbrt, double.NegativeInfinity, double.NegativeInfinity, "the cube root of an infinity")),
    FloatOperation.Binary(IrIntrinsicOp.Hypot, Family, FloatContract.Ulp(1), nameof(FloatOperations.Hypot), FloatOperations.HypotF, FloatOperations.Hypot)
      .With(Special.Of(IrIntrinsicOp.Hypot, [double.PositiveInfinity, double.NaN], double.PositiveInfinity, "an infinity wins over a NaN (IEEE 754 §9.2.1)"),
        Special.Of(IrIntrinsicOp.Hypot, [double.NaN, double.NegativeInfinity], double.PositiveInfinity, "an infinity wins over a NaN in either place"),
        Special.Of(IrIntrinsicOp.Hypot, [Special.NegativeZero, Special.NegativeZero], 0.0, "the length of a zero vector is +0"),
        Special.Of(IrIntrinsicOp.Hypot, [-3.0, 4.0], 5.0, "an exact length is exact")),
    FloatOperation.Unary(IrIntrinsicOp.ReciprocalSqrt, Family, FloatContract.Ulp(1), nameof(FloatOperations.ReciprocalSqrt),
      FloatOperations.ReciprocalSqrtF, FloatOperations.ReciprocalSqrt)
      .With(Special.Of(IrIntrinsicOp.ReciprocalSqrt, Special.NegativeZero, double.PositiveInfinity, "rSqrt(-0) is +∞ (IEEE 754 §9.2.1)"),
        Special.Of(IrIntrinsicOp.ReciprocalSqrt, -1.0, double.NaN, "a negative operand has no root"),
        Special.Of(IrIntrinsicOp.ReciprocalSqrt, double.PositiveInfinity, 0.0, "rSqrt(+∞) is +0"),
        Special.Of(IrIntrinsicOp.ReciprocalSqrt, 4.0, 0.5, "an exact result is exact"))
  ];

}
