using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// The trigonometric functions, each <c>ulp(1)</c> under ADR 0005 and evaluated by a correctly
/// rounded portable implementation converted from CORE-MATH. <c>SinCos</c> of a source is its two
/// parts.
/// </summary>
internal static class TrigonometricFamily {

  private const string Family = "Trigonometric";

  /// <summary>The operations of the family.</summary>
  public static IReadOnlyList<FloatOperation> Operations { get; } = [
    FloatOperation.Unary(IrIntrinsicOp.Sin, Family, FloatContract.Ulp(1), nameof(FloatOperations.Sin), FloatOperations.SinF, FloatOperations.Sin)
      .With(Special.Of(IrIntrinsicOp.Sin, Special.NegativeZero, Special.NegativeZero, "the sine of -0 is -0"),
        Special.Of(IrIntrinsicOp.Sin, double.PositiveInfinity, double.NaN, "the sine of an infinity is a NaN")),
    FloatOperation.Unary(IrIntrinsicOp.Cos, Family, FloatContract.Ulp(1), nameof(FloatOperations.Cos), FloatOperations.CosF, FloatOperations.Cos)
      .With(Special.Of(IrIntrinsicOp.Cos, Special.NegativeZero, 1.0, "the cosine of a zero is 1"),
        Special.Of(IrIntrinsicOp.Cos, double.NegativeInfinity, double.NaN, "the cosine of an infinity is a NaN")),
    FloatOperation.Unary(IrIntrinsicOp.Tan, Family, FloatContract.Ulp(1), nameof(FloatOperations.Tan), FloatOperations.TanF, FloatOperations.Tan)
      .With(Special.Of(IrIntrinsicOp.Tan, Special.NegativeZero, Special.NegativeZero, "the tangent of -0 is -0"),
        Special.Of(IrIntrinsicOp.Tan, double.PositiveInfinity, double.NaN, "the tangent of an infinity is a NaN"))
  ];

}
