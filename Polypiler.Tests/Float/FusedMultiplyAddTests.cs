using System;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Float;

/// <summary>
/// The fused multiply-add where it is hardest: an addend that nearly cancels the product, results in
/// the subnormal range and at the smallest normal number, and the float sums that land halfway between
/// two floats. .NET's <c>Math.FusedMultiplyAdd</c>, correctly rounded, is the oracle.
/// </summary>
[TestFixture]
public sealed class FusedMultiplyAddTests {

  [Test]
  public void NearCancellationAndSubnormalResults_AreCorrectlyRounded() {
    var state = FloatSamples.Seed;
    var failures = 0;
    var first = "";
    for (var index = 0; index < 200_000; ++index) {
      var x = Draw(ref state, index % 4 == 0 ? -540 : -10, 20);
      var y = Draw(ref state, index % 4 == 0 ? -540 : -10, 20);
      var product = x * y;
      // The negated product moved by a few steps, or by a tiny amount, or a value far below or above it.
      var z = (index % 5) switch {
        0 => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(-product) + (long)(FloatSamples.Next(ref state) % 7) - 3),
        1 => -product + Draw(ref state, -1074, -1000),
        2 => Draw(ref state, -1074, -1020),
        3 => -product * (1 + Math.ScaleB(1, -(int)(FloatSamples.Next(ref state) % 60))),
        _ => Draw(ref state, -60, 60)
      };
      var expected = Math.FusedMultiplyAdd(x, y, z);
      var actual = FloatOperations.FusedMultiplyAdd(x, y, z);
      if (BitConverter.DoubleToInt64Bits(expected) != BitConverter.DoubleToInt64Bits(actual) && !(double.IsNaN(expected) && double.IsNaN(actual))) {
        if (failures++ == 0)
          first = $"fma({x:R}, {y:R}, {z:R}) = {expected:R}, got {actual:R}";
      }
    }

    Assert.That(failures, Is.Zero, first);
  }

  /// <summary>The products that round to the smallest normal number from just below it.</summary>
  [TestCase(1.4916681462400413E-154, 1.4916681462400413E-154, -2.2250738585072014E-308)]
  [TestCase(2.2250738585072009E-308, 1.0000000000000002, 0.0)]
  [TestCase(-2.2250738585072009E-308, 1.0000000000000002, 4.9406564584124654E-324)]
  [TestCase(1.0000000000000002, 2.2250738585072004E-308, -4.9406564584124654E-324)]
  public void ResultsAtTheSmallestNormal_AreCorrectlyRounded(double x, double y, double z)
    => Assert.That(BitConverter.DoubleToInt64Bits(FloatOperations.FusedMultiplyAdd(x, y, z)), Is.EqualTo(BitConverter.DoubleToInt64Bits(Math.FusedMultiplyAdd(x, y, z))));

  /// <summary>
  /// Sums whose double lies halfway between two floats although the exact sum does not: 24929 * 43072
  /// is 2^30 + 2^6, halfway between 2^30 and 2^30 + 2^7, and the tiny addend decides the side.
  /// </summary>
  [TestCase(24929f, 43072f, 9.094947E-13f, 1073741952f)]
  [TestCase(24929f, 43072f, -9.094947E-13f, 1073741824f)]
  [TestCase(-24929f, 43072f, 9.094947E-13f, -1073741824f)]
  [TestCase(-24929f, 43072f, -9.094947E-13f, -1073741952f)]
  [TestCase(4.000018119812012f, 3.9999818801879883f, 345575456f, float.NaN)]
  [TestCase(-2.000033378601074f, -0.9999833106994629f, -66450792f, float.NaN)]
  [TestCase(-4.000089168548584f, 7.999821662902832f, 830978944f, float.NaN)]
  [TestCase(8.000040054321289f, -7.999959945678711f, -1636559232f, float.NaN)]
  public void FloatSumsHalfwayBetweenTwoFloats_GoTowardsTheExactSum(float x, float y, float z, float expected) {
    var oracle = MathF.FusedMultiplyAdd(x, y, z);
    if (!float.IsNaN(expected))
      Assert.That(oracle, Is.EqualTo(expected));

    Assert.That(BitConverter.SingleToInt32Bits(FloatOperations.FusedMultiplyAddF(x, y, z)), Is.EqualTo(BitConverter.SingleToInt32Bits(oracle)));
    Assert.That(IrInterpreter.Call(FloatImplementations.Module, "FusedMultiplyAddF", [x, y, z]), Is.EqualTo(oracle));
  }

  [Test]
  public void FloatSumsBelowHalfTheSmallestFloat_RoundAsTheExactSum() {
    var state = FloatSamples.Seed + 5;
    var failures = "";
    for (var index = 0; index < 100_000; ++index) {
      var x = (float)Draw(ref state, -80, -60);
      var y = (float)Draw(ref state, -100, -60);
      var z = index % 2 == 0 ? (float)Draw(ref state, -149, -120) : -(x * y);
      if (BitConverter.SingleToInt32Bits(FloatOperations.FusedMultiplyAddF(x, y, z)) != BitConverter.SingleToInt32Bits(MathF.FusedMultiplyAdd(x, y, z)))
        failures += $"{x:R} {y:R} {z:R}; ";
    }

    Assert.That(failures, Is.Empty);
  }

  [Test]
  public void MultiplyHigh_IsTheHighHalfOfTheProduct() {
    var state = FloatSamples.Seed;
    ulong[] edges = [0, 1, 0xffffffffUL, 0x100000000UL, 0xffffffff00000000UL, ulong.MaxValue, 0x8000000000000000UL];
    foreach (var a in edges)
      foreach (var b in edges)
        Assert.That(FloatOperations.MultiplyHigh(a, b), Is.EqualTo(Math.BigMul(a, b, out _)));

    var wrong = 0;
    for (var index = 0; index < 10_000; ++index) {
      var a = FloatSamples.Next(ref state);
      var b = FloatSamples.Next(ref state);
      if (FloatOperations.MultiplyHigh(a, b) != Math.BigMul(a, b, out _))
        ++wrong;
    }

    Assert.That(wrong, Is.Zero);
  }

  /// <summary>A random number of either sign whose binary exponent is in [low, high).</summary>
  private static double Draw(ref ulong state, int low, int high) {
    var bits = FloatSamples.Next(ref state);
    var exponent = low + (int)(bits % (ulong)(high - low));
    var value = Math.ScaleB(1 + (double)(bits >> 12) / (1UL << 52), exponent);
    return (bits & 2048) != 0 ? -value : value;
  }

}
