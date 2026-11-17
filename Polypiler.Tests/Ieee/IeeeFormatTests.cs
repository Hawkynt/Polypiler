using System;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Ieee;

/// <summary>
/// #116: the generic registry of floating-point formats. Every operation of <see cref="IeeeFormat"/>
/// works on any widths; <c>binary16</c>, which the IR has no type of yet, is registered here and checked
/// against .NET's <see cref="Half"/> as the oracle.
/// </summary>
[TestFixture]
public sealed class IeeeFormatTests {

  private static readonly IeeeFormat _half = new("binary16", 5, 10);

  [Test]
  public void TheDefaultRegistry_HoldsBinary32AndBinary64() {
    Assert.That(IeeeFormats.Default.All, Is.EqualTo(new[] { IeeeFormats.Binary32, IeeeFormats.Binary64 }));
    Assert.That(IeeeFormats.Default.Of(IrType.F64), Is.SameAs(IeeeFormats.Binary64));
    Assert.That(IeeeFormats.Default.Of(IrType.I32), Is.Null);
    Assert.That(IeeeFormats.Default.Named("binary32"), Is.SameAs(IeeeFormats.Binary32));
    Assert.That(IeeeFormats.Default.Named("binary16"), Is.Null);
    Assert.That(IeeeFormats.Binary64.ToString(), Is.EqualTo("binary64"));
    Assert.That((IeeeFormats.Binary64.Bias, IeeeFormats.Binary64.MinExponent, IeeeFormats.Binary64.StorageBits), Is.EqualTo((1023, -1022, 64)));
    Assert.That((IeeeFormats.Binary32.Bias, IeeeFormats.Binary32.MaxValue, IeeeFormats.Binary32.CanonicalNaN), Is.EqualTo((127, 0x7F7FFFFFUL, 0x7FC00000UL)));
  }

  [Test]
  public void AFormatOfItsOwn_Registers() {
    var registry = new IeeeFormats().Register(_half);
    Assert.That(registry.Named("binary16"), Is.SameAs(_half));
    Assert.That(registry.All, Has.Count.EqualTo(1));
    Assert.That(() => registry.Register(new IeeeFormat("binary16", 5, 10)), Throws.ArgumentException.With.Message.Contains("binary16"));
    Assert.That(() => new IeeeFormats().Register(IeeeFormats.Binary32).Register(new IeeeFormat("other", 8, 23, IrType.F32)), Throws.ArgumentException.With.Message.Contains("f32"));
    Assert.That(() => registry.Register(null!), Throws.ArgumentNullException);
  }

  [TestCase(1, 10)]
  [TestCase(21, 10)]
  [TestCase(5, 0)]
  [TestCase(11, 53)]
  public void WidthsOutsideTheirRange_AreRefused(int exponent, int fraction)
    => Assert.That(() => new IeeeFormat("x", exponent, fraction), Throws.TypeOf<ArgumentOutOfRangeException>());

  [Test]
  public void TheWidestFormat_FillsSixtyFourBits() {
    var wide = new IeeeFormat("wide", 20, 43);
    Assert.That(wide.StorageBits, Is.EqualTo(64));
    Assert.That(() => new IeeeFormat(" ", 5, 10), Throws.ArgumentException);
  }

  [Test]
  public void Round_RefusesANegativeNumeratorAndANonPositiveDenominator() {
    Assert.That(() => _half.Round(false, BigInteger.MinusOne, BigInteger.One, RoundingMode.Up), Throws.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(() => _half.Round(false, BigInteger.One, BigInteger.Zero, RoundingMode.Up), Throws.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(_half.Round(true, BigInteger.Zero, BigInteger.One, RoundingMode.Up), Is.EqualTo(0x8000UL), "a zero keeps its sign");
  }

  /// <summary>A double rounded to <c>binary16</c> to nearest even is .NET's conversion; the other modes bracket it.</summary>
  [Test]
  public void Binary16_ConvertsAsHalfDoes() {
    var random = new Random(1616);
    for (var index = 0; index < 20000; ++index) {
      var value = (random.NextDouble() - 0.5) * Math.Pow(2, random.Next(-30, 20));
      var bits = BitConverter.DoubleToUInt64Bits(value);
      var nearest = _half.Convert(bits, IeeeFormats.Binary64, RoundingMode.NearestEven);
      Assert.That(nearest, Is.EqualTo((ulong)BitConverter.HalfToUInt16Bits((Half)value)), $"{value:R}");
      var up = (double)BitConverter.UInt16BitsToHalf((ushort)_half.Convert(bits, IeeeFormats.Binary64, RoundingMode.Up));
      var down = (double)BitConverter.UInt16BitsToHalf((ushort)_half.Convert(bits, IeeeFormats.Binary64, RoundingMode.Down));
      Assert.That(down <= value && value <= up, Is.True, $"{down:R} <= {value:R} <= {up:R}");
    }

    Assert.That(_half.Convert(BitConverter.DoubleToUInt64Bits(double.NaN), IeeeFormats.Binary64, RoundingMode.Up), Is.EqualTo(_half.CanonicalNaN));
    Assert.That(_half.Convert(BitConverter.DoubleToUInt64Bits(double.NegativeInfinity), IeeeFormats.Binary64, RoundingMode.Up), Is.EqualTo(0xFC00UL));
    Assert.That(_half.Convert(BitConverter.DoubleToUInt64Bits(1e6), IeeeFormats.Binary64, RoundingMode.TowardZero), Is.EqualTo(0x7BFFUL), "65504, the largest half");
    Assert.That(() => _half.Convert(0, null!, RoundingMode.Up), Throws.ArgumentNullException);
  }

  /// <summary>The sum of two halves is exact as a double, so .NET's conversion of it is the correctly rounded half sum.</summary>
  [Test]
  public void Binary16_AddsAndMultipliesAsHalfDoes() {
    var random = new Random(1617);
    for (var index = 0; index < 20000; ++index) {
      var left = (ushort)random.Next(0x10000);
      var right = (ushort)random.Next(0x10000);
      var (x, y) = ((double)BitConverter.UInt16BitsToHalf(left), (double)BitConverter.UInt16BitsToHalf(right));
      if (double.IsNaN(x) || double.IsNaN(y))
        continue;

      Assert.That(_half.Add(left, right, RoundingMode.NearestEven), Is.EqualTo(Expected(x + y)), $"{x:R} + {y:R}");
      Assert.That(_half.Multiply(left, right, RoundingMode.NearestEven), Is.EqualTo(Expected(x * y)), $"{x:R} * {y:R}");
    }

    ulong Expected(double exact) => double.IsNaN(exact) ? _half.CanonicalNaN : BitConverter.HalfToUInt16Bits((Half)exact);
  }

  [Test]
  public void Integers_ConvertToAFormatAndBack() {
    var single = IeeeFormats.Binary32;
    Assert.That(single.FromInteger(16777217, RoundingMode.NearestEven), Is.EqualTo((ulong)BitConverter.SingleToUInt32Bits(16777216f)));
    Assert.That(single.FromInteger(16777217, RoundingMode.Up), Is.EqualTo((ulong)BitConverter.SingleToUInt32Bits(16777218f)));
    Assert.That(single.FromInteger(-16777217, RoundingMode.Up), Is.EqualTo((ulong)BitConverter.SingleToUInt32Bits(-16777216f)));
    Assert.That(single.FromInteger(0, RoundingMode.Down), Is.EqualTo(0UL));
    var bits = (ulong)BitConverter.SingleToUInt32Bits(2.5f);
    Assert.That(new[] { RoundingMode.NearestEven, RoundingMode.TowardZero, RoundingMode.Up, RoundingMode.Down, RoundingMode.NearestAway }
      .Select(mode => single.ToInteger(bits, mode)), Is.EqualTo(new BigInteger?[] { 2, 2, 3, 2, 3 }));
    var negative = (ulong)BitConverter.SingleToUInt32Bits(-2.5f);
    Assert.That(new[] { RoundingMode.NearestEven, RoundingMode.TowardZero, RoundingMode.Up, RoundingMode.Down, RoundingMode.NearestAway }
      .Select(mode => single.ToInteger(negative, mode)), Is.EqualTo(new BigInteger?[] { -2, -2, -2, -3, -3 }));
    Assert.That(single.ToInteger((ulong)BitConverter.SingleToUInt32Bits(3.5f), RoundingMode.NearestEven), Is.EqualTo((BigInteger?)4));
    Assert.That(single.ToInteger((ulong)BitConverter.SingleToUInt32Bits(1e30f), RoundingMode.NearestEven), Is.EqualTo((BigInteger?)(BigInteger)1e30f));
    Assert.That(single.ToInteger(single.PositiveInfinity, RoundingMode.NearestEven), Is.Null);
    Assert.That(single.ToInteger(single.CanonicalNaN, RoundingMode.NearestEven), Is.Null);
  }

  [Test]
  public void TheBitsOfAHostValue_AreThoseOfItsFormat() {
    Assert.That(IeeeFormats.BitsOf(1.0f), Is.EqualTo(0x3F800000UL));
    Assert.That(IeeeFormats.BitsOf(-2.0), Is.EqualTo(0xC000000000000000UL));
    Assert.That(() => IeeeFormats.BitsOf(1), Throws.ArgumentException);
    Assert.That(IeeeFormats.ValueOf(IrType.F32, 0x3F800000UL), Is.EqualTo(1.0f));
    Assert.That(IeeeFormats.ValueOf(IrType.F64, 0xC000000000000000UL), Is.EqualTo(-2.0));
  }

  [Test]
  public void TheShortNames_ParseAndPrint() {
    foreach (var mode in Enum.GetValues<RoundingMode>())
      Assert.That(IeeeNames.TryParse(IeeeNames.Of(mode), out RoundingMode parsed) && parsed == mode, Is.True, mode.ToString());

    Assert.That(IeeeNames.Of(RoundingMode.TowardZero), Is.EqualTo("rtz"));
    Assert.That(IeeeNames.Of(FloatPredicate.Unordered), Is.EqualTo("uno"));
    Assert.That(IeeeNames.TryParse("rzz", out RoundingMode _), Is.False);
    Assert.That(IeeeNames.TryParse("xyz", out FloatPredicate _), Is.False);
    Assert.That(() => IeeeNames.Of((RoundingMode)5), Throws.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(() => IeeeNames.Of((RoundingMode)(-1)), Throws.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(() => IeeeNames.Of((FloatPredicate)14), Throws.TypeOf<ArgumentOutOfRangeException>());
  }

}
