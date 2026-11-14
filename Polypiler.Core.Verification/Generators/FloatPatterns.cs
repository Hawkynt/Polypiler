using System.Collections.Generic;

namespace Polypiler.Core.Verification.Generators;

/// <summary>
/// IEEE 754 binary32 and binary64 bit patterns: one representative of every class of value first,
/// then seeded random patterns, which reach every binade, both signs and every NaN payload.
/// </summary>
public static class FloatPatterns {

  /// <summary>
  /// The binary32 edges, in order: +0, −0, the smallest and the largest subnormal, the smallest
  /// normal, 1.0, the largest finite value, +∞, −∞, the quiet NaN and a signalling NaN.
  /// </summary>
  public static IReadOnlyList<uint> Edges32 { get; } = [
    0x00000000, 0x80000000, 0x00000001, 0x007FFFFF, 0x00800000, 0x3F800000,
    0x7F7FFFFF, 0x7F800000, 0xFF800000, 0x7FC00000, 0x7F800001
  ];

  /// <summary>The binary64 edges, in the order of <see cref="Edges32"/>.</summary>
  public static IReadOnlyList<ulong> Edges64 { get; } = [
    0x0000000000000000, 0x8000000000000000, 0x0000000000000001, 0x000FFFFFFFFFFFFF,
    0x0010000000000000, 0x3FF0000000000000, 0x7FEFFFFFFFFFFFFF, 0x7FF0000000000000,
    0xFFF0000000000000, 0x7FF8000000000000, 0x7FF0000000000001
  ];

  /// <summary>The binary32 edges, then an endless run of random binary32 patterns.</summary>
  public static IEnumerable<uint> Sequence32(ulong seed) {
    foreach (var edge in Edges32)
      yield return edge;

    var random = new SeededRandom(seed);
    while (true)
      yield return (uint)random.NextBits(32);
  }

  /// <summary>The binary64 edges, then an endless run of random binary64 patterns.</summary>
  public static IEnumerable<ulong> Sequence64(ulong seed) {
    foreach (var edge in Edges64)
      yield return edge;

    var random = new SeededRandom(seed);
    while (true)
      yield return random.NextUInt64();
  }

}
