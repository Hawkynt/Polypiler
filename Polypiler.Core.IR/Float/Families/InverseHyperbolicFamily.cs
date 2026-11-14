using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// The inverse hyperbolic (area) functions, each <c>ulp(1)</c> under ADR 0005 and evaluated by a
/// correctly rounded portable implementation converted from CORE-MATH.
/// </summary>
internal static class InverseHyperbolicFamily {

  private const string Family = "Inverse hyperbolic";

  /// <summary>The operations of the family.</summary>
  public static IReadOnlyList<FloatOperation> Operations { get; } = [
    FloatOperation.Unary(IrIntrinsicOp.Asinh, Family, FloatContract.Ulp(1), nameof(FloatOperations.Asinh), FloatOperations.AsinhF, FloatOperations.Asinh)
      .With(Special.Of(IrIntrinsicOp.Asinh, Special.NegativeZero, Special.NegativeZero, "asinh keeps the sign of zero"),
        Special.Of(IrIntrinsicOp.Asinh, double.NegativeInfinity, double.NegativeInfinity, "asinh keeps an infinity")),
    FloatOperation.Unary(IrIntrinsicOp.Acosh, Family, FloatContract.Ulp(1), nameof(FloatOperations.Acosh), FloatOperations.AcoshF, FloatOperations.Acosh)
      .With(Special.Of(IrIntrinsicOp.Acosh, 1.0, 0.0, "acosh(1) is +0"),
        Special.Of(IrIntrinsicOp.Acosh, 0.5, double.NaN, "below 1 there is no area cosine"),
        Special.Of(IrIntrinsicOp.Acosh, double.PositiveInfinity, double.PositiveInfinity, "acosh(+∞) is +∞")),
    FloatOperation.Unary(IrIntrinsicOp.Atanh, Family, FloatContract.Ulp(1), nameof(FloatOperations.Atanh), FloatOperations.AtanhF, FloatOperations.Atanh)
      .With(Special.Of(IrIntrinsicOp.Atanh, 1.0, double.PositiveInfinity, "atanh(1) is +∞"),
        Special.Of(IrIntrinsicOp.Atanh, -1.0, double.NegativeInfinity, "atanh(-1) is -∞"),
        Special.Of(IrIntrinsicOp.Atanh, Special.NegativeZero, Special.NegativeZero, "atanh keeps the sign of zero"),
        Special.Of(IrIntrinsicOp.Atanh, 2.0, double.NaN, "outside [-1, 1] there is no area tangent"))
  ];

}
