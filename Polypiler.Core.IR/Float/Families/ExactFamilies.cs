using System;
using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// The operations whose result is exact: the arithmetic of IEEE 754-2019 §5.4.1, the rounding, the
/// sign and magnitude, the representation, the classification, the angle conversion and the
/// interpolation. Every one has one result on every target, compared bit for bit.
/// </summary>
internal static class ExactFamilies {

  private const string Arithmetic = "Exact arithmetic";
  private const string Rounding = "Rounding";
  private const string Sign = "Sign and magnitude";
  private const string Representation = "Representation";
  private const string Classification = "Classification";
  private const string Angle = "Angle conversion";
  private const string Interpolation = "Interpolation";

  private static readonly FloatContract _exact = FloatContract.Exact;

  /// <summary>The operations, family by family.</summary>
  public static IReadOnlyList<FloatOperation> Operations { get; } = [
    FloatOperation.Existing(IrIntrinsicOp.Sqrt, Arithmetic, FloatShape.Unary, _exact)
      .With(Special.Of(IrIntrinsicOp.Sqrt, Special.NegativeZero, Special.NegativeZero, "the square root of -0 is -0")),
    FloatOperation.Ternary(IrIntrinsicOp.FusedMultiplyAdd, Arithmetic, _exact, nameof(FloatOperations.FusedMultiplyAdd),
      FloatOperations.FusedMultiplyAddF, FloatOperations.FusedMultiplyAdd),
    FloatOperation.Binary(IrIntrinsicOp.IeeeRemainder, Arithmetic, _exact, nameof(FloatOperations.IeeeRemainder),
      FloatOperations.IeeeRemainderF, FloatOperations.IeeeRemainder)
      .With(Special.Of(IrIntrinsicOp.IeeeRemainder, [Special.NegativeZero, 1.0], Special.NegativeZero, "a zero remainder has the sign of the dividend")),
    FloatOperation.Unary(IrIntrinsicOp.Reciprocal, Arithmetic, _exact, nameof(FloatOperations.Reciprocal), FloatOperations.ReciprocalF, FloatOperations.Reciprocal),

    FloatOperation.Existing(IrIntrinsicOp.Floor, Rounding, FloatShape.Unary, _exact),
    FloatOperation.Existing(IrIntrinsicOp.Ceiling, Rounding, FloatShape.Unary, _exact),
    FloatOperation.Existing(IrIntrinsicOp.Truncate, Rounding, FloatShape.Unary, _exact),
    FloatOperation.Existing(IrIntrinsicOp.RoundEven, Rounding, FloatShape.Unary, _exact),
    FloatOperation.Existing(IrIntrinsicOp.RoundAway, Rounding, FloatShape.Unary, _exact),
    FloatOperation.Existing(IrIntrinsicOp.RoundHalfUp, Rounding, FloatShape.Unary, _exact),
    FloatOperation.Of(IrIntrinsicOp.RoundDigits, Rounding, FloatShape.WithDigits, _exact, nameof(FloatOperations.RoundDigits),
      arguments => FloatOperations.RoundDigitsF((float)arguments[0], (int)(long)arguments[1], (int)(long)arguments[2]),
      arguments => FloatOperations.RoundDigits((double)arguments[0], (int)(long)arguments[1], (int)(long)arguments[2]),
      arguments => (ulong)(long)arguments[1] > (arguments[0] is float ? 6UL : 15UL)),

    FloatOperation.Existing(IrIntrinsicOp.Abs, Sign, FloatShape.Unary, _exact),
    FloatOperation.Existing(IrIntrinsicOp.Sign, Sign, FloatShape.ToInteger, _exact),
    FloatOperation.Existing(IrIntrinsicOp.Min, Sign, FloatShape.Binary, _exact),
    FloatOperation.Existing(IrIntrinsicOp.Max, Sign, FloatShape.Binary, _exact),
    FloatOperation.Binary(IrIntrinsicOp.CopySign, Sign, _exact, nameof(FloatOperations.CopySign), FloatOperations.CopySignF, FloatOperations.CopySign),
    FloatOperation.Binary(IrIntrinsicOp.MinMagnitude, Sign, _exact, nameof(FloatOperations.MinMagnitude), FloatOperations.MinMagnitudeF, FloatOperations.MinMagnitude),
    FloatOperation.Binary(IrIntrinsicOp.MaxMagnitude, Sign, _exact, nameof(FloatOperations.MaxMagnitude), FloatOperations.MaxMagnitudeF, FloatOperations.MaxMagnitude),
    FloatOperation.Binary(IrIntrinsicOp.MinNumber, Sign, _exact, nameof(FloatOperations.MinNumber), FloatOperations.MinNumberF, FloatOperations.MinNumber)
      .With(Special.Of(IrIntrinsicOp.MinNumber, [double.NaN, 3.0], 3.0, "a NaN operand is ignored"),
        Special.Of(IrIntrinsicOp.MinNumber, [0.0, Special.NegativeZero], Special.NegativeZero, "-0 is smaller than +0")),
    FloatOperation.Binary(IrIntrinsicOp.MaxNumber, Sign, _exact, nameof(FloatOperations.MaxNumber), FloatOperations.MaxNumberF, FloatOperations.MaxNumber)
      .With(Special.Of(IrIntrinsicOp.MaxNumber, [Special.NegativeZero, 0.0], 0.0, "+0 is larger than -0")),
    FloatOperation.Ternary(IrIntrinsicOp.Clamp, Sign, _exact, nameof(FloatOperations.Clamp), FloatOperations.ClampF, FloatOperations.Clamp,
      arguments => arguments[0] is float ? (float)arguments[1] > (float)arguments[2] : (double)arguments[1] > (double)arguments[2]),

    FloatOperation.WithInteger(IrIntrinsicOp.ScaleB, Representation, _exact, nameof(FloatOperations.ScaleB), FloatOperations.ScaleBF, FloatOperations.ScaleB),
    FloatOperation.ToInteger(IrIntrinsicOp.ILogB, Representation, nameof(FloatOperations.ILogB), FloatOperations.ILogBF, FloatOperations.ILogB),
    FloatOperation.Unary(IrIntrinsicOp.BitIncrement, Representation, _exact, nameof(FloatOperations.BitIncrement), FloatOperations.BitIncrementF, FloatOperations.BitIncrement),
    FloatOperation.Unary(IrIntrinsicOp.BitDecrement, Representation, _exact, nameof(FloatOperations.BitDecrement), FloatOperations.BitDecrementF, FloatOperations.BitDecrement),
    FloatOperation.Existing(IrIntrinsicOp.FloatBits, Representation, FloatShape.ToBits, _exact),
    FloatOperation.Existing(IrIntrinsicOp.BitsFloat, Representation, FloatShape.FromBits, _exact),
    FloatOperation.Unary(IrIntrinsicOp.Fraction, Representation, _exact, nameof(FloatOperations.Fraction), FloatOperations.FractionF, FloatOperations.Fraction),
    FloatOperation.ToInteger(IrIntrinsicOp.Exponent, Representation, nameof(FloatOperations.Exponent), FloatOperations.ExponentF, FloatOperations.Exponent),

    FloatOperation.Predicate(IrIntrinsicOp.IsNaN, Classification, nameof(FloatOperations.IsNaN), FloatOperations.IsNaNF, FloatOperations.IsNaN),
    FloatOperation.Predicate(IrIntrinsicOp.IsInfinity, Classification, nameof(FloatOperations.IsInfinity), FloatOperations.IsInfinityF, FloatOperations.IsInfinity),
    FloatOperation.Predicate(IrIntrinsicOp.IsFinite, Classification, nameof(FloatOperations.IsFinite), FloatOperations.IsFiniteF, FloatOperations.IsFinite),
    FloatOperation.Predicate(IrIntrinsicOp.IsNegative, Classification, nameof(FloatOperations.IsNegative), FloatOperations.IsNegativeF, FloatOperations.IsNegative),
    FloatOperation.Predicate(IrIntrinsicOp.IsNormal, Classification, nameof(FloatOperations.IsNormal), FloatOperations.IsNormalF, FloatOperations.IsNormal),
    FloatOperation.Predicate(IrIntrinsicOp.IsSubnormal, Classification, nameof(FloatOperations.IsSubnormal), FloatOperations.IsSubnormalF, FloatOperations.IsSubnormal),
    FloatOperation.Predicate(IrIntrinsicOp.IsInteger, Classification, nameof(FloatOperations.IsInteger), FloatOperations.IsIntegerF, FloatOperations.IsInteger),

    FloatOperation.Unary(IrIntrinsicOp.DegreesToRadians, Angle, _exact, nameof(FloatOperations.DegreesToRadians),
      FloatOperations.DegreesToRadiansF, FloatOperations.DegreesToRadians),
    FloatOperation.Unary(IrIntrinsicOp.RadiansToDegrees, Angle, _exact, nameof(FloatOperations.RadiansToDegrees),
      FloatOperations.RadiansToDegreesF, FloatOperations.RadiansToDegrees),

    FloatOperation.Ternary(IrIntrinsicOp.Lerp, Interpolation, _exact, nameof(FloatOperations.Lerp), FloatOperations.LerpF, FloatOperations.Lerp)
  ];

}

/// <summary>Builds the special values of the tables, in both formats from one value of <c>double</c>.</summary>
internal static class Special {

  /// <summary>The negative zero.</summary>
  public const double NegativeZero = -0.0;

  /// <summary>The special value of <c>RootN</c> in both formats: the integer operand is carried as its value.</summary>
  public static FloatSpecialValue[] Rooted(double operand, int n, double result, string note) => [
    new(IrIntrinsicOp.RootN, IrType.F64, [IrOperators.Bits(IrType.F64, operand), n], IrOperators.Bits(IrType.F64, result), note),
    new(IrIntrinsicOp.RootN, IrType.F32, [IrOperators.Bits(IrType.F32, operand), n], IrOperators.Bits(IrType.F32, result), note)
  ];

  /// <summary>The special value of an operation with one operand, in both formats.</summary>
  public static FloatSpecialValue[] Of(IrIntrinsicOp op, double operand, double result, string note) => Of(op, [operand], result, note);

  /// <summary>The special value of an operation, in both formats: every operand and the result rounded to each.</summary>
  public static FloatSpecialValue[] Of(IrIntrinsicOp op, double[] operands, double result, string note) => [
    new(op, IrType.F64, Array.ConvertAll(operands, operand => IrOperators.Bits(IrType.F64, operand)), IrOperators.Bits(IrType.F64, result), note),
    new(op, IrType.F32, Array.ConvertAll(operands, operand => IrOperators.Bits(IrType.F32, operand)), IrOperators.Bits(IrType.F32, result), note)
  ];

}
