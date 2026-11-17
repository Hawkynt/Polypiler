using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Polypiler.Core.IR;

/// <summary>
/// A binary interchange format of IEEE 754-2019 (§3.6), given by the widths of its exponent and its
/// fraction: <c>binary32</c> and <c>binary64</c> are the two the IR has types of, but every operation
/// here is defined for any width, so another format plugs in by registering its widths
/// (<see cref="IeeeFormats"/>). Values are carried as their bits in the low bits of a <see cref="ulong"/>.
/// <para>
/// The arithmetic is exact: each operation forms the exact result as a rational number and rounds it
/// once, in the mode it is given, so no host rounding takes part. A NaN result is the canonical quiet
/// NaN of the format: sign clear, exponent all ones, only the most significant fraction bit set.
/// </para>
/// </summary>
public sealed class IeeeFormat {

  /// <summary>Describes a format.</summary>
  /// <param name="name">Its name, as <c>binary64</c>.</param>
  /// <param name="exponentBits">The width of the exponent field, 2 to 20.</param>
  /// <param name="fractionBits">The width of the fraction field, the significand without its leading bit: at least 1, and at most what keeps a value within 64 bits.</param>
  /// <param name="type">The type of the IR whose values are of the format, or <see langword="null"/> for a format the IR has no type of.</param>
  /// <exception cref="ArgumentException">The name is empty.</exception>
  /// <exception cref="ArgumentOutOfRangeException">A width lies outside its range, or the format is wider than 64 bits.</exception>
  public IeeeFormat(string name, int exponentBits, int fractionBits, IrType? type = null) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    ArgumentOutOfRangeException.ThrowIfLessThan(exponentBits, 2);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(exponentBits, 20);
    ArgumentOutOfRangeException.ThrowIfLessThan(fractionBits, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(fractionBits, 63 - exponentBits);
    this.Name = name;
    this.ExponentBits = exponentBits;
    this.FractionBits = fractionBits;
    this.Type = type;
  }

  /// <summary>The name of the format.</summary>
  public string Name { get; }

  /// <summary>The width of the exponent field.</summary>
  public int ExponentBits { get; }

  /// <summary>The width of the fraction field: the precision less one.</summary>
  public int FractionBits { get; }

  /// <summary>The type of the IR of the format, or <see langword="null"/>.</summary>
  public IrType? Type { get; }

  /// <summary>The width of a value: sign, exponent and fraction.</summary>
  public int StorageBits => 1 + this.ExponentBits + this.FractionBits;

  /// <summary>The bias of the exponent field, <c>2^(w-1) - 1</c>; also the largest exponent of a finite number.</summary>
  public int Bias => (1 << (this.ExponentBits - 1)) - 1;

  /// <summary>The exponent of the smallest normal number, <c>1 - bias</c>.</summary>
  public int MinExponent => 1 - this.Bias;

  /// <summary>The sign bit.</summary>
  public ulong SignMask => 1UL << (this.StorageBits - 1);

  /// <summary>The bits of the exponent field.</summary>
  public ulong ExponentMask => ((1UL << this.ExponentBits) - 1) << this.FractionBits;

  /// <summary>The bits of the fraction field.</summary>
  public ulong FractionMask => (1UL << this.FractionBits) - 1;

  /// <summary>The bits of positive infinity.</summary>
  public ulong PositiveInfinity => this.ExponentMask;

  /// <summary>The bits of the largest finite number.</summary>
  public ulong MaxValue => this.ExponentMask - 1;

  /// <summary>The bits of the canonical quiet NaN.</summary>
  public ulong CanonicalNaN => this.ExponentMask | (1UL << (this.FractionBits - 1));

  /// <summary>Whether bits are a NaN.</summary>
  /// <param name="bits">The bits.</param>
  public bool IsNaN(ulong bits) => (bits & this.ExponentMask) == this.ExponentMask && (bits & this.FractionMask) != 0;

  /// <summary>Whether bits are an infinity.</summary>
  /// <param name="bits">The bits.</param>
  public bool IsInfinity(ulong bits) => (bits & ~this.SignMask) == this.ExponentMask;

  /// <summary>Whether bits are a zero.</summary>
  /// <param name="bits">The bits.</param>
  public bool IsZero(ulong bits) => (bits & ~this.SignMask) == 0;

  /// <summary>Whether the sign bit is set.</summary>
  /// <param name="bits">The bits.</param>
  public bool IsNegative(ulong bits) => (bits & this.SignMask) != 0;

  /// <summary>The class of a value: exactly one of zero, subnormal, normal, infinite, quiet and signalling NaN, and negative where the sign bit is set.</summary>
  /// <param name="bits">The bits.</param>
  /// <returns>The class.</returns>
  public FloatClass Classify(ulong bits) {
    var exponent = bits & this.ExponentMask;
    var fraction = bits & this.FractionMask;
    var kind = exponent == this.ExponentMask
      ? fraction == 0 ? FloatClass.Infinite : (fraction >> (this.FractionBits - 1)) != 0 ? FloatClass.QuietNaN : FloatClass.SignalingNaN
      : exponent != 0 ? FloatClass.Normal
      : fraction != 0 ? FloatClass.Subnormal : FloatClass.Zero;
    return this.IsNegative(bits) ? kind | FloatClass.Negative : kind;
  }

  /// <summary>The exact value of a finite number as <c>(-1)^negative × significand × 2^exponent</c>.</summary>
  /// <param name="bits">The bits of a finite number.</param>
  /// <returns>The sign, the integral significand and the exponent of its last bit.</returns>
  public (bool Negative, BigInteger Significand, int Exponent) Decompose(ulong bits) {
    var field = (int)((bits & this.ExponentMask) >> this.FractionBits);
    var fraction = bits & this.FractionMask;
    var significand = field == 0 ? fraction : fraction | (1UL << this.FractionBits);
    return (this.IsNegative(bits), significand, Math.Max(field, 1) - this.Bias - this.FractionBits);
  }

  /// <summary>
  /// The value of the format nearest a positive or zero rational number in a rounding mode, with a sign:
  /// the one correct rounding of IEEE 754-2019 §4.3, overflow giving infinity or the largest finite
  /// number as the mode says (§7.4), and a zero result keeping the sign.
  /// </summary>
  /// <param name="negative">Whether the number is negative.</param>
  /// <param name="numerator">The numerator of its magnitude, not negative.</param>
  /// <param name="denominator">The denominator of its magnitude, positive.</param>
  /// <param name="mode">The rounding mode.</param>
  /// <returns>The bits of the rounded number.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The numerator is negative or the denominator not positive.</exception>
  public ulong Round(bool negative, BigInteger numerator, BigInteger denominator, RoundingMode mode) {
    ArgumentOutOfRangeException.ThrowIfNegative(numerator);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominator);
    var sign = negative ? this.SignMask : 0;
    if (numerator.IsZero)
      return sign;

    // The exponent of the leading bit: 2^e <= n/d < 2^(e+1).
    var exponent = (int)(numerator.GetBitLength() - denominator.GetBitLength());
    if (Scaled(numerator, denominator, -exponent).Quotient.IsZero)
      --exponent;

    // The weight of the last bit of the significand the rounded number will have.
    var quantum = Math.Max(exponent, this.MinExponent) - this.FractionBits;
    var (integral, remainder, divisor) = Scaled(numerator, denominator, -quantum);
    if (RoundsAway(negative, integral, remainder, divisor, mode))
      ++integral;

    if (integral >> (this.FractionBits + 1) != 0) {
      // Rounding carried into a new leading bit; the last bit dropped is a zero.
      integral >>= 1;
      ++quantum;
    }

    if (integral >> this.FractionBits == 0)
      return sign | (ulong)integral;

    var field = quantum + this.FractionBits + this.Bias;
    if (field >= (1 << this.ExponentBits) - 1)
      return sign | this.Overflow(negative, mode);

    return sign | ((ulong)field << this.FractionBits) | ((ulong)integral & this.FractionMask);
  }

  /// <summary>The value of the format nearest <c>(-1)^negative × magnitude × 2^exponent</c>.</summary>
  /// <param name="negative">Whether the number is negative.</param>
  /// <param name="magnitude">The integral magnitude, not negative.</param>
  /// <param name="exponent">The power of two it is scaled by.</param>
  /// <param name="mode">The rounding mode.</param>
  /// <returns>The bits of the rounded number.</returns>
  public ulong Round(bool negative, BigInteger magnitude, int exponent, RoundingMode mode)
    => exponent >= 0 ? this.Round(negative, magnitude << exponent, BigInteger.One, mode) : this.Round(negative, magnitude, BigInteger.One << -exponent, mode);

  /// <summary>The sum of two values, rounded once.</summary>
  /// <param name="left">The bits of the first summand.</param>
  /// <param name="right">The bits of the second summand.</param>
  /// <param name="mode">The rounding mode.</param>
  /// <returns>The bits of the sum; an exact zero sum is <c>+0</c>, or <c>-0</c> in <see cref="RoundingMode.Down"/> or when both summands are <c>-0</c> (§6.3).</returns>
  public ulong Add(ulong left, ulong right, RoundingMode mode) {
    if (this.IsNaN(left) || this.IsNaN(right))
      return this.CanonicalNaN;

    if (this.IsInfinity(left) || this.IsInfinity(right)) {
      if (this.IsInfinity(left) && this.IsInfinity(right) && this.IsNegative(left) != this.IsNegative(right))
        return this.CanonicalNaN;

      return this.IsInfinity(left) ? left : right;
    }

    var (leftNegative, leftSignificand, leftExponent) = this.Decompose(left);
    var (rightNegative, rightSignificand, rightExponent) = this.Decompose(right);
    var exponent = Math.Min(leftExponent, rightExponent);
    var sum = Signed(leftNegative, leftSignificand << (leftExponent - exponent)) + Signed(rightNegative, rightSignificand << (rightExponent - exponent));
    if (sum.IsZero)
      // Either both summands are -0, or one of them is negative: in rdn the zero is -0 then.
      return (leftNegative && rightNegative) || (mode == RoundingMode.Down && (leftNegative || rightNegative)) ? this.SignMask : 0;

    return this.Round(sum.Sign < 0, BigInteger.Abs(sum), exponent, mode);
  }

  /// <summary>The difference of two values, rounded once: the sum with the sign of the subtrahend changed.</summary>
  /// <param name="left">The bits of the minuend.</param>
  /// <param name="right">The bits of the subtrahend.</param>
  /// <param name="mode">The rounding mode.</param>
  /// <returns>The bits of the difference.</returns>
  public ulong Subtract(ulong left, ulong right, RoundingMode mode) => this.Add(left, right ^ this.SignMask, mode);

  /// <summary>The product of two values, rounded once.</summary>
  /// <param name="left">The bits of the first factor.</param>
  /// <param name="right">The bits of the second factor.</param>
  /// <param name="mode">The rounding mode.</param>
  /// <returns>The bits of the product.</returns>
  public ulong Multiply(ulong left, ulong right, RoundingMode mode) {
    if (this.IsNaN(left) || this.IsNaN(right))
      return this.CanonicalNaN;

    var sign = (left ^ right) & this.SignMask;
    if (this.IsInfinity(left) || this.IsInfinity(right))
      return this.IsZero(left) || this.IsZero(right) ? this.CanonicalNaN : sign | this.PositiveInfinity;

    var (negative, leftSignificand, leftExponent) = this.Decompose(left);
    var (_, rightSignificand, rightExponent) = this.Decompose(right);
    return this.Round(negative != this.IsNegative(right), leftSignificand * rightSignificand, leftExponent + rightExponent, mode);
  }

  /// <summary>The quotient of two values, rounded once.</summary>
  /// <param name="left">The bits of the dividend.</param>
  /// <param name="right">The bits of the divisor.</param>
  /// <param name="mode">The rounding mode.</param>
  /// <returns>The bits of the quotient: an infinity for a finite non-zero dividend and a zero divisor, a NaN for 0/0 and ∞/∞.</returns>
  public ulong Divide(ulong left, ulong right, RoundingMode mode) {
    if (this.IsNaN(left) || this.IsNaN(right))
      return this.CanonicalNaN;

    var sign = (left ^ right) & this.SignMask;
    if (this.IsInfinity(left))
      return this.IsInfinity(right) ? this.CanonicalNaN : sign | this.PositiveInfinity;

    if (this.IsInfinity(right))
      return sign;

    if (this.IsZero(right))
      return this.IsZero(left) ? this.CanonicalNaN : sign | this.PositiveInfinity;

    var (_, leftSignificand, leftExponent) = this.Decompose(left);
    var (_, rightSignificand, rightExponent) = this.Decompose(right);
    var shift = leftExponent - rightExponent;
    return shift >= 0
      ? this.Round(sign != 0, leftSignificand << shift, rightSignificand, mode)
      : this.Round(sign != 0, leftSignificand, rightSignificand << -shift, mode);
  }

  /// <summary>
  /// The order of two values that are no NaN: negative, zero or positive as the first is below, equal
  /// to or above the second; the two zeros are equal.
  /// </summary>
  /// <param name="left">The bits of the first value.</param>
  /// <param name="right">The bits of the second value.</param>
  /// <returns>The order.</returns>
  public int Order(ulong left, ulong right) {
    if (this.IsZero(left) && this.IsZero(right))
      return 0;

    // The bits of the magnitude grow with it; the sign reverses the order.
    long Key(ulong bits) => this.IsNegative(bits) ? -(long)(bits & ~this.SignMask) : (long)bits;
    return Key(left).CompareTo(Key(right));
  }

  /// <summary>A comparison of two values: ordered ones are false and unordered ones true when an operand is a NaN.</summary>
  /// <param name="left">The bits of the first value.</param>
  /// <param name="right">The bits of the second value.</param>
  /// <param name="predicate">The comparison.</param>
  /// <returns>The truth value.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The value is no predicate.</exception>
  public bool Compare(ulong left, ulong right, FloatPredicate predicate) {
    var unordered = this.IsNaN(left) || this.IsNaN(right);
    if (predicate is FloatPredicate.Ordered or FloatPredicate.Unordered)
      return unordered == (predicate == FloatPredicate.Unordered);

    if (unordered)
      return predicate switch {
        >= FloatPredicate.OrderedEqual and <= FloatPredicate.OrderedGreaterOrEqual => false,
        >= FloatPredicate.UnorderedEqual and <= FloatPredicate.UnorderedGreaterOrEqual => true,
        _ => throw new ArgumentOutOfRangeException(nameof(predicate), predicate, "no predicate")
      };

    var order = this.Order(left, right);
    return predicate switch {
      FloatPredicate.OrderedEqual or FloatPredicate.UnorderedEqual => order == 0,
      FloatPredicate.OrderedNotEqual or FloatPredicate.UnorderedNotEqual => order != 0,
      FloatPredicate.OrderedLess or FloatPredicate.UnorderedLess => order < 0,
      FloatPredicate.OrderedLessOrEqual or FloatPredicate.UnorderedLessOrEqual => order <= 0,
      FloatPredicate.OrderedGreater or FloatPredicate.UnorderedGreater => order > 0,
      FloatPredicate.OrderedGreaterOrEqual or FloatPredicate.UnorderedGreaterOrEqual => order >= 0,
      _ => throw new ArgumentOutOfRangeException(nameof(predicate), predicate, "no predicate")
    };
  }

  /// <summary>An integer as the value of the format nearest it, rounded once.</summary>
  /// <param name="value">The integer.</param>
  /// <param name="mode">The rounding mode.</param>
  /// <returns>The bits; 0 is <c>+0</c>.</returns>
  public ulong FromInteger(BigInteger value, RoundingMode mode) => this.Round(value.Sign < 0, BigInteger.Abs(value), 0, mode);

  /// <summary>A finite value rounded to an integer, once, in a mode; <see langword="null"/> for a NaN or an infinity.</summary>
  /// <param name="bits">The bits.</param>
  /// <param name="mode">The rounding mode.</param>
  /// <returns>The integer.</returns>
  public BigInteger? ToInteger(ulong bits, RoundingMode mode) {
    if ((bits & this.ExponentMask) == this.ExponentMask)
      return null;

    var (negative, significand, exponent) = this.Decompose(bits);
    if (exponent >= 0)
      return Signed(negative, significand << exponent);

    var (integral, remainder, divisor) = Scaled(significand, BigInteger.One, exponent);
    return Signed(negative, RoundsAway(negative, integral, remainder, divisor, mode) ? integral + 1 : integral);
  }

  /// <summary>A value of another format as the value of this one nearest it, rounded once; a NaN becomes the canonical NaN.</summary>
  /// <param name="bits">The bits of the value.</param>
  /// <param name="source">The format of the value.</param>
  /// <param name="mode">The rounding mode.</param>
  /// <returns>The bits in this format.</returns>
  public ulong Convert(ulong bits, IeeeFormat source, RoundingMode mode) {
    ArgumentNullException.ThrowIfNull(source);
    if (source.IsNaN(bits))
      return this.CanonicalNaN;

    var sign = source.IsNegative(bits) ? this.SignMask : 0;
    if (source.IsInfinity(bits))
      return sign | this.PositiveInfinity;

    var (negative, significand, exponent) = source.Decompose(bits);
    return this.Round(negative, significand, exponent, mode);
  }

  /// <summary>The name of the format.</summary>
  public override string ToString() => this.Name;

  /// <summary>What overflow gives in a mode, without the sign: infinity, or the largest finite number where the mode rounds towards zero for the sign.</summary>
  private ulong Overflow(bool negative, RoundingMode mode) => mode switch {
    RoundingMode.TowardZero => this.MaxValue,
    RoundingMode.Up => negative ? this.MaxValue : this.PositiveInfinity,
    RoundingMode.Down => negative ? this.PositiveInfinity : this.MaxValue,
    _ => this.PositiveInfinity
  };

  /// <summary>
  /// Whether a magnitude cut to <paramref name="integral"/> with <paramref name="remainder"/> of
  /// <paramref name="divisor"/> left over rounds to the next integer away from zero in a mode.
  /// </summary>
  private static bool RoundsAway(bool negative, BigInteger integral, BigInteger remainder, BigInteger divisor, RoundingMode mode) {
    if (remainder.IsZero)
      return false;

    var half = (remainder << 1).CompareTo(divisor);
    return mode switch {
      RoundingMode.NearestEven => half > 0 || (half == 0 && !integral.IsEven),
      RoundingMode.NearestAway => half >= 0,
      RoundingMode.Up => !negative,
      RoundingMode.Down => negative,
      _ => false
    };
  }

  private static BigInteger Signed(bool negative, BigInteger magnitude) => negative ? -magnitude : magnitude;

  /// <summary><c>n × 2^shift / d</c> as an integral quotient and a remainder of the divisor it gives.</summary>
  private static (BigInteger Quotient, BigInteger Remainder, BigInteger Divisor) Scaled(BigInteger numerator, BigInteger denominator, int shift) {
    var (top, bottom) = shift >= 0 ? (numerator << shift, denominator) : (numerator, denominator << -shift);
    var quotient = BigInteger.DivRem(top, bottom, out var remainder);
    return (quotient, remainder, bottom);
  }

}

/// <summary>
/// The registry of floating-point formats: each format by its name and, where the IR has one, by its
/// type. <see cref="Default"/> holds <c>binary32</c> and <c>binary64</c>; a further format (#384
/// <c>binary16</c>, #131 <c>binary128</c>) registers its widths in a registry of its own and every
/// operation of <see cref="IeeeFormat"/> works on it unchanged.
/// </summary>
public sealed class IeeeFormats {

  private readonly Dictionary<string, IeeeFormat> _byName = new(StringComparer.Ordinal);
  private readonly List<IeeeFormat> _all = [];

  /// <summary>IEEE 754 binary32, the format of <see cref="IrType.F32"/>.</summary>
  public static IeeeFormat Binary32 { get; } = new("binary32", 8, 23, IrType.F32);

  /// <summary>IEEE 754 binary64, the format of <see cref="IrType.F64"/>.</summary>
  public static IeeeFormat Binary64 { get; } = new("binary64", 11, 52, IrType.F64);

  /// <summary>The registry of the formats the IR has types of.</summary>
  public static IeeeFormats Default { get; } = new IeeeFormats().Register(Binary32).Register(Binary64);

  /// <summary>Every format, in the order of registration.</summary>
  public IReadOnlyList<IeeeFormat> All => this._all;

  /// <summary>Adds a format.</summary>
  /// <param name="format">The format.</param>
  /// <returns>The registry, for chaining.</returns>
  /// <exception cref="ArgumentException">A format of the name, or of the type, is registered already.</exception>
  public IeeeFormats Register(IeeeFormat format) {
    ArgumentNullException.ThrowIfNull(format);
    if (format.Type is { } type && this.Of(type) is not null)
      throw new ArgumentException($"A format of the type {type} is registered already.", nameof(format));
    if (!this._byName.TryAdd(format.Name, format))
      throw new ArgumentException($"A format named '{format.Name}' is registered already.", nameof(format));

    this._all.Add(format);
    return this;
  }

  /// <summary>The format of a name, or <see langword="null"/>.</summary>
  /// <param name="name">The name.</param>
  /// <returns>The format.</returns>
  public IeeeFormat? Named(string name) => this._byName.GetValueOrDefault(name);

  /// <summary>The format of a type of the IR, or <see langword="null"/> for a type of no registered format.</summary>
  /// <param name="type">The type.</param>
  /// <returns>The format.</returns>
  public IeeeFormat? Of(IrType type) => this._all.FirstOrDefault(format => format.Type == type);

  /// <summary>The bits of a value as the interpreter carries one of <see cref="IrType.F32"/> or <see cref="IrType.F64"/>.</summary>
  /// <param name="value">A <see cref="float"/> or a <see cref="double"/>.</param>
  /// <returns>The bits.</returns>
  /// <exception cref="ArgumentException">The value is neither.</exception>
  public static ulong BitsOf(object value) => value switch {
    float single => BitConverter.SingleToUInt32Bits(single),
    double number => BitConverter.DoubleToUInt64Bits(number),
    _ => throw new ArgumentException($"{value} is no float or double.", nameof(value))
  };

  /// <summary>The value the interpreter carries for bits of a type.</summary>
  /// <param name="type"><see cref="IrType.F32"/> or <see cref="IrType.F64"/>.</param>
  /// <param name="bits">The bits.</param>
  /// <returns>A <see cref="float"/> or a <see cref="double"/>.</returns>
  public static object ValueOf(IrType type, ulong bits)
    => type == IrType.F32 ? (object)BitConverter.UInt32BitsToSingle((uint)bits) : BitConverter.UInt64BitsToDouble(bits);

}
