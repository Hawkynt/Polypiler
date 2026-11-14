using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// The hyperbolic functions, each <c>ulp(2)</c> under ADR 0005 (the reference bound of its native
/// slack table) and evaluated by a correctly rounded portable implementation converted from CORE-MATH.
/// </summary>
internal static class HyperbolicFamily {

  private const string Family = "Hyperbolic";

  /// <summary>The operations of the family.</summary>
  public static IReadOnlyList<FloatOperation> Operations { get; } = [
    FloatOperation.Unary(IrIntrinsicOp.Sinh, Family, FloatContract.Ulp(2), nameof(FloatOperations.Sinh), FloatOperations.SinhF, FloatOperations.Sinh)
      .With(Special.Of(IrIntrinsicOp.Sinh, Special.NegativeZero, Special.NegativeZero, "sinh(-0) is -0"),
        Special.Of(IrIntrinsicOp.Sinh, double.NegativeInfinity, double.NegativeInfinity, "sinh keeps an infinity")),
    FloatOperation.Unary(IrIntrinsicOp.Cosh, Family, FloatContract.Ulp(2), nameof(FloatOperations.Cosh), FloatOperations.CoshF, FloatOperations.Cosh)
      .With(Special.Of(IrIntrinsicOp.Cosh, Special.NegativeZero, 1.0, "cosh of a zero is 1"),
        Special.Of(IrIntrinsicOp.Cosh, double.NegativeInfinity, double.PositiveInfinity, "cosh of an infinity is +∞")),
    FloatOperation.Unary(IrIntrinsicOp.Tanh, Family, FloatContract.Ulp(2), nameof(FloatOperations.Tanh), FloatOperations.TanhF, FloatOperations.Tanh)
      .With(Special.Of(IrIntrinsicOp.Tanh, Special.NegativeZero, Special.NegativeZero, "tanh(-0) is -0"),
        Special.Of(IrIntrinsicOp.Tanh, double.NegativeInfinity, -1.0, "tanh of an infinity is ±1"))
  ];

}
