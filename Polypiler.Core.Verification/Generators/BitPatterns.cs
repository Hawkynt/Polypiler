using System;
using System.Collections.Generic;

namespace Polypiler.Core.Verification.Generators;

/// <summary>
/// Integer bit patterns of every width from 1 to 64, read as unsigned values in a <see cref="ulong"/>:
/// the edge values where wrap-around and sign changes happen first, then seeded random patterns.
/// </summary>
public static class BitPatterns {

  /// <summary>The narrowest width.</summary>
  public const int MinWidth = 1;

  /// <summary>The widest width.</summary>
  public const int MaxWidth = 64;

  /// <summary>
  /// The edge values of a width, in this order and without duplicates: 0, 1, the largest signed
  /// value 2^(w−1) − 1, the smallest signed value 2^(w−1), 2^w − 2 and all ones 2^w − 1.
  /// </summary>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is outside 1…64.</exception>
  public static IReadOnlyList<ulong> Edges(int width) {
    CheckWidth(width);
    var allOnes = Mask(width);
    var signBit = 1UL << (width - 1);
    var edges = new List<ulong>(6);
    foreach (var value in (ReadOnlySpan<ulong>)[0, 1, signBit - 1, signBit, allOnes - 1, allOnes])
      if (!edges.Contains(value))
        edges.Add(value);

    return edges;
  }

  /// <summary>The edge values of the width, then an endless run of random patterns of that width.</summary>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is outside 1…64.</exception>
  public static IEnumerable<ulong> Sequence(int width, ulong seed) {
    var edges = Edges(width);
    return Enumerate(width, seed, edges);
  }

  /// <summary>A pattern of the width read as a two's-complement signed value: <c>0x80</c> at width 8 is −128.</summary>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is outside 1…64.</exception>
  public static long AsSigned(ulong value, int width) {
    CheckWidth(width);
    var shift = 64 - width;
    return (long)(value << shift) >> shift;
  }

  /// <summary>The pattern with every bit of the width set.</summary>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is outside 1…64.</exception>
  public static ulong Mask(int width) {
    CheckWidth(width);
    return ulong.MaxValue >> (64 - width);
  }

  internal static void CheckWidth(int width) {
    ArgumentOutOfRangeException.ThrowIfLessThan(width, MinWidth);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaxWidth);
  }

  private static IEnumerable<ulong> Enumerate(int width, ulong seed, IReadOnlyList<ulong> edges) {
    foreach (var edge in edges)
      yield return edge;

    var random = new SeededRandom(seed);
    while (true)
      yield return random.NextBits(width);
  }

}
