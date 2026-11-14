using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Float.Support;

/// <summary>
/// Operands for the exact operations: the boundary set of every format and random values from
/// SplitMix64 seeded with 2026, so every run sees the same ones.
/// </summary>
internal static class FloatSamples {

  /// <summary>The seed of the random operands.</summary>
  public const ulong Seed = 2026;

  /// <summary>
  /// The boundary set: both zeros and infinities, a NaN, the smallest and largest subnormal, the
  /// smallest normal, the largest finite value, 1 and its neighbours, the ties of rounding and a few
  /// ordinary numbers, each with both signs.
  /// </summary>
  public static IReadOnlyList<double> Boundaries(IrType format) {
    var f32 = format == IrType.F32;
    double[] positive = [
      0.0, double.PositiveInfinity, double.NaN,
      f32 ? float.Epsilon : double.Epsilon,
      f32 ? BitsOf(format, 0x007fffff) : BitsOf(format, 0x000fffffffffffff),
      f32 ? BitsOf(format, 0x00800000) : BitsOf(format, 0x0010000000000000),
      f32 ? float.MaxValue : double.MaxValue,
      1.0, f32 ? BitsOf(format, 0x3f800001) : BitsOf(format, 0x3ff0000000000001), f32 ? BitsOf(format, 0x3f7fffff) : BitsOf(format, 0x3fefffffffffffff),
      0.5, 1.5, 2.5, 3.5, 4503599627370495.5, 4503599627370496.5, 8388607.5, 8388608.5, 0.25, 2.0, 3.0, 10.0, 1e10, 1e-10, 0.1, 1.005, 2.675
    ];
    return [.. positive.Select(value => Round(format, value)).SelectMany(value => new[] { value, -value }).DistinctBy(BitConverter.DoubleToInt64Bits)];
  }

  /// <summary>Random numbers of a format: random bits, so every binade, subnormals, infinities and NaNs occur.</summary>
  public static IReadOnlyList<double> Random(IrType format, int count, ulong stream = 0) {
    var state = Seed + stream;
    var values = new List<double>();
    for (var index = 0; index < count; ++index) {
      var bits = Next(ref state);
      // Half of the values are drawn near 1, where most arithmetic happens and ties occur.
      if (index % 2 == 1)
        bits = format == IrType.F64 ? (bits & 0x800fffffffffffffUL) | ((ulong)(0x3f0 + (long)(bits >> 52 & 31)) << 52) : (bits & 0x807fffffUL) | ((ulong)(0x70 + (long)(bits >> 23 & 31)) << 23);

      values.Add(format == IrType.F64 ? FloatVectors.Value(format, (long)bits) : FloatVectors.Value(format, (long)(bits & 0xffffffffUL)));
    }

    return values;
  }

  /// <summary>The boundary set and 1 000 random values.</summary>
  public static IReadOnlyList<double> All(IrType format, ulong stream = 0) => [.. Boundaries(format), .. Random(format, 1000, stream)];

  /// <summary>SplitMix64.</summary>
  public static ulong Next(ref ulong state) {
    var z = state += 0x9E3779B97F4A7C15UL;
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
    return z ^ (z >> 31);
  }

  /// <summary>A number rounded to a format.</summary>
  public static double Round(IrType format, double value) => format == IrType.F32 ? (float)value : value;

  private static double BitsOf(IrType format, long bits) => FloatVectors.Value(format, bits);

}
