using System;
using System.Globalization;
using System.Numerics;

namespace Polypiler.Core.Verification.Oracles;

/// <summary>Why an element did not pass.</summary>
public enum ElementFailure {

  /// <summary>It passed.</summary>
  None,

  /// <summary>The special-value rule rejected it: a NaN, an infinity or the sign of a zero.</summary>
  SpecialValues,

  /// <summary>The policy is <see cref="ExactnessPolicy.Exact"/> and the bits differ.</summary>
  Bits,

  /// <summary>The two values are farther apart than the tolerance allows.</summary>
  Tolerance

}

/// <summary>The verdict on one floating-point element.</summary>
/// <param name="Failure">Why it failed; <see cref="ElementFailure.None"/> when it passed.</param>
/// <param name="Distance">The comparison distance of two numbers; <see langword="null"/> when either is a NaN or an infinity.</param>
/// <param name="Excess">For <see cref="ElementFailure.Tolerance"/>, the measured difference as a message shows it: the distance in <c>ulp</c> mode, otherwise the difference or the relative difference.</param>
public sealed record ElementComparison(ElementFailure Failure, ulong? Distance, string? Excess = null) {

  /// <summary>Whether the element passed.</summary>
  public bool Passed => this.Failure == ElementFailure.None;

}

/// <summary>
/// The element comparison of <c>docs/adr/0005-floating-point-tolerance.md</c>: the special-value rule
/// first, then the policy's mode on two numbers. Values are bit patterns, so nothing is rounded before
/// it is compared.
/// </summary>
public static class FloatComparison {

  /// <summary>
  /// The comparison distance of two numbers: the absolute difference of their positions in IEEE
  /// total order (IEEE 754-2019 §5.10), with <c>-0</c> and <c>+0</c> at the same position. One step
  /// is one representable value, whatever its size.
  /// </summary>
  /// <param name="format">The format of both values.</param>
  /// <param name="a">The bits of one value; for <see cref="FloatFormat.F32"/> the low 32 bits.</param>
  /// <param name="b">The bits of the other.</param>
  /// <returns>The number of steps from one to the other.</returns>
  public static ulong Distance(FloatFormat format, ulong a, ulong b) {
    var (signA, magnitudeA) = Split(format, a);
    var (signB, magnitudeB) = Split(format, b);
    if (magnitudeA == 0 || magnitudeB == 0 || signA == signB)
      return magnitudeA > magnitudeB ? magnitudeA - magnitudeB : magnitudeB - magnitudeA;

    return magnitudeA + magnitudeB;
  }

  /// <summary>Compares one element of an actual output with the reference's.</summary>
  /// <param name="format">The format of both values.</param>
  /// <param name="reference">The bits of the reference value; for <see cref="FloatFormat.F32"/> the low 32 bits.</param>
  /// <param name="actual">The bits of the actual value.</param>
  /// <param name="policy">The mode and the special-value rule; a tolerance must be of <paramref name="format"/>.</param>
  /// <returns>The verdict.</returns>
  /// <exception cref="ArgumentException">The policy is a tolerance over another format.</exception>
  public static ElementComparison Compare(FloatFormat format, ulong reference, ulong actual, ExactnessPolicy policy) {
    ArgumentNullException.ThrowIfNull(policy);
    if (policy is ExactnessPolicy.Tolerance tolerance && tolerance.Format != format)
      throw new ArgumentException($"the policy compares {tolerance.Format} values, the element is {format}", nameof(policy));

    var mask = format == FloatFormat.F32 ? 0xffff_ffffUL : ulong.MaxValue;
    reference &= mask;
    actual &= mask;
    var referenceClass = Classify(format, reference);
    var actualClass = Classify(format, actual);
    if (referenceClass == ValueClass.NaN || actualClass == ValueClass.NaN) {
      var same = referenceClass == actualClass && (policy.SpecialValues != SpecialValues.Bitwise || reference == actual);
      return new(same ? ElementFailure.None : ElementFailure.SpecialValues, null);
    }

    if (referenceClass == ValueClass.Infinity || actualClass == ValueClass.Infinity)
      return new(reference == actual ? ElementFailure.None : ElementFailure.SpecialValues, null);

    var distance = Distance(format, reference, actual);
    if (reference == actual)
      return new(ElementFailure.None, 0);

    if (referenceClass == ValueClass.Zero && actualClass == ValueClass.Zero)
      return new(policy.SpecialValues == SpecialValues.Class ? ElementFailure.None : ElementFailure.SpecialValues, 0);

    return policy switch {
      ExactnessPolicy.Ulp ulp => distance <= (ulong)ulp.Bound
        ? new(ElementFailure.None, distance)
        : new(ElementFailure.Tolerance, distance, distance.ToString(CultureInfo.InvariantCulture)),
      ExactnessPolicy.Relative relative => Rational(format, reference, actual, relative.Bound, true, distance),
      ExactnessPolicy.Absolute absolute => Rational(format, reference, actual, absolute.Bound, false, distance),
      _ => new(ElementFailure.Bits, distance)
    };
  }

  /// <summary>The value of bits of a format, widened to <see cref="double"/> without rounding.</summary>
  /// <param name="format">The format.</param>
  /// <param name="bits">The bits; for <see cref="FloatFormat.F32"/> the low 32 bits.</param>
  public static double Value(FloatFormat format, ulong bits)
    => format == FloatFormat.F32 ? BitConverter.UInt32BitsToSingle((uint)bits) : BitConverter.UInt64BitsToDouble(bits);

  /// <summary>
  /// <c>|a - r| &lt;= bound * |r|</c> or <c>|a - r| &lt;= bound</c> on the exact values: every finite
  /// binary floating-point number is an integer times a power of two, so both sides are scaled to the
  /// smaller power and compared as integers.
  /// </summary>
  private static ElementComparison Rational(FloatFormat format, ulong reference, ulong actual, double bound, bool relative, ulong distance) {
    var r = Dyadic(Value(format, reference));
    var a = Dyadic(Value(format, actual));
    var b = Dyadic(bound);
    var differenceExponent = Math.Min(r.Exponent, a.Exponent);
    var difference = BigInteger.Abs((a.Mantissa << (a.Exponent - differenceExponent)) - (r.Mantissa << (r.Exponent - differenceExponent)));
    var (limit, limitExponent) = relative
      ? (BigInteger.Abs(b.Mantissa * r.Mantissa), b.Exponent + r.Exponent)
      : (b.Mantissa, b.Exponent);
    var common = Math.Min(differenceExponent, limitExponent);
    if (difference << (differenceExponent - common) <= limit << (limitExponent - common))
      return new(ElementFailure.None, distance);

    var actualValue = Value(format, actual);
    var referenceValue = Value(format, reference);
    var shown = relative ? Math.Abs(actualValue - referenceValue) / Math.Abs(referenceValue) : Math.Abs(actualValue - referenceValue);
    return new(ElementFailure.Tolerance, distance, shown.ToString("R", CultureInfo.InvariantCulture));
  }

  /// <summary>A finite double as <c>mantissa * 2^exponent</c>, the mantissa signed.</summary>
  private static (BigInteger Mantissa, int Exponent) Dyadic(double value) {
    var bits = BitConverter.DoubleToUInt64Bits(value);
    var field = (int)(bits >> 52) & 0x7ff;
    var fraction = bits & 0xf_ffff_ffff_ffffUL;
    var (mantissa, exponent) = field == 0 ? (fraction, -1074) : (fraction | 1UL << 52, field - 1075);
    return ((bits >> 63) != 0 ? -new BigInteger(mantissa) : new BigInteger(mantissa), exponent);
  }

  private enum ValueClass { Zero, Finite, Infinity, NaN }

  private static (bool Negative, ulong Magnitude) Split(FloatFormat format, ulong bits)
    => format == FloatFormat.F32
      ? ((bits & 0x8000_0000UL) != 0, bits & 0x7fff_ffffUL)
      : ((bits >> 63) != 0, bits & 0x7fff_ffff_ffff_ffffUL);

  private static ValueClass Classify(FloatFormat format, ulong bits) {
    var (_, magnitude) = Split(format, bits);
    var infinity = format == FloatFormat.F32 ? 0x7f80_0000UL : 0x7ff0_0000_0000_0000UL;
    if (magnitude == 0)
      return ValueClass.Zero;

    if (magnitude < infinity)
      return ValueClass.Finite;

    return magnitude == infinity ? ValueClass.Infinity : ValueClass.NaN;
  }

}
