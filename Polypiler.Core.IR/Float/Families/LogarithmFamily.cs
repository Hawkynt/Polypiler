using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// The logarithms, each <c>ulp(1)</c> under ADR 0005 and evaluated by a correctly rounded portable
/// implementation converted from CORE-MATH, and <c>LogBase</c>, which is .NET's definition over the
/// correctly rounded <c>Log</c>: at most 2 values away from the correctly rounded quotient, so <c>ulp(2)</c>.
/// <c>Log2</c> of a floating-point number shares its operation with the bit position of an integer.
/// </summary>
internal static class LogarithmFamily {

  private const string Family = "Logarithm";

  /// <summary>The operations of the family.</summary>
  public static IReadOnlyList<FloatOperation> Operations { get; } = [
    FloatOperation.Unary(IrIntrinsicOp.Log, Family, FloatContract.Ulp(1), nameof(FloatOperations.Log), FloatOperations.LogF, FloatOperations.Log)
      .With(Special.Of(IrIntrinsicOp.Log, 0.0, double.NegativeInfinity, "log(+0) is -∞"),
        Special.Of(IrIntrinsicOp.Log, Special.NegativeZero, double.NegativeInfinity, "log(-0) is -∞"),
        Special.Of(IrIntrinsicOp.Log, -1.0, double.NaN, "the logarithm of a negative number is a NaN"),
        Special.Of(IrIntrinsicOp.Log, 1.0, 0.0, "log(1) is +0")),
    FloatOperation.Unary(IrIntrinsicOp.Log2, Family, FloatContract.Ulp(1), nameof(FloatOperations.Log2), FloatOperations.Log2F, FloatOperations.Log2)
      .With(Special.Of(IrIntrinsicOp.Log2, 1024.0, 10.0, "a power of two gives its exponent exactly"),
        Special.Of(IrIntrinsicOp.Log2, Special.NegativeZero, double.NegativeInfinity, "log2(-0) is -∞")),
    FloatOperation.Unary(IrIntrinsicOp.Log10, Family, FloatContract.Ulp(1), nameof(FloatOperations.Log10), FloatOperations.Log10F, FloatOperations.Log10)
      .With(Special.Of(IrIntrinsicOp.Log10, 1000.0, 3.0, "a power of ten gives its exponent exactly"),
        Special.Of(IrIntrinsicOp.Log10, double.PositiveInfinity, double.PositiveInfinity, "log10(+∞) is +∞")),
    FloatOperation.Unary(IrIntrinsicOp.Log1P, Family, FloatContract.Ulp(1), nameof(FloatOperations.Log1P), FloatOperations.Log1PF, FloatOperations.Log1P)
      .With(Special.Of(IrIntrinsicOp.Log1P, -1.0, double.NegativeInfinity, "log1p(-1) is -∞"),
        Special.Of(IrIntrinsicOp.Log1P, Special.NegativeZero, Special.NegativeZero, "log1p keeps the sign of zero"),
        Special.Of(IrIntrinsicOp.Log1P, -2.0, double.NaN, "below -1 there is no logarithm")),
    FloatOperation.Binary(IrIntrinsicOp.LogBase, Family, new FloatContract(FloatContractKind.Ulp, 2, 2), nameof(FloatOperations.LogBase),
      FloatOperations.LogBaseF, FloatOperations.LogBase)
      .With(Special.Of(IrIntrinsicOp.LogBase, [8.0, 2.0], 3.0, "an exact quotient of exact logarithms"),
        Special.Of(IrIntrinsicOp.LogBase, [2.0, 1.0], double.NaN, "base 1 is a NaN (.NET)"),
        Special.Of(IrIntrinsicOp.LogBase, [1.0, 1.0], double.NaN, "base 1 is a NaN even of 1 (.NET)"),
        Special.Of(IrIntrinsicOp.LogBase, [2.0, 0.0], double.NaN, "base 0 is a NaN unless a is 1 (.NET)"),
        Special.Of(IrIntrinsicOp.LogBase, [2.0, double.PositiveInfinity], double.NaN, "base +∞ is a NaN unless a is 1 (.NET)"),
        Special.Of(IrIntrinsicOp.LogBase, [1.0, double.PositiveInfinity], 0.0, "log of 1 in base +∞ is 0 (.NET)"))
  ];

}
