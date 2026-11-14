using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Verification.Generators;

/// <summary>
/// Deterministic shrink candidates, smallest first: a runner that keeps the first candidate which
/// still fails and repeats ends at a minimal failing value without drawing a single random number.
/// </summary>
public static class Shrink {

  /// <summary>
  /// The candidates for an unsigned value: 0, then <c>v − v/2</c>, <c>v − v/4</c>, … while the
  /// subtracted term is at least 1, ending with <c>v − 1</c>. Nothing for 0. Every candidate is
  /// smaller than the value, so a pattern of a width stays a pattern of that width.
  /// </summary>
  public static IEnumerable<ulong> Integer(ulong value) {
    if (value == 0)
      yield break;

    yield return 0;
    for (var term = value / 2; term >= 1; term /= 2)
      yield return value - term;
  }

  /// <summary>
  /// The candidates for a buffer, each different from the buffer and from every earlier candidate:
  /// the empty buffer, the first and the second half, the buffer without each single byte, then the
  /// buffer with each byte shrunk as an <see cref="Integer"/>. Nothing for the empty buffer.
  /// </summary>
  /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
  public static IEnumerable<byte[]> Buffer(byte[] value) {
    ArgumentNullException.ThrowIfNull(value);
    return Distinct(value, BufferCandidates(value));
  }

  private static IEnumerable<byte[]> BufferCandidates(byte[] value) {
    yield return [];
    var half = value.Length / 2;
    yield return value[..half];
    yield return value[half..];
    for (var index = 0; index < value.Length; ++index)
      yield return [.. value[..index], .. value[(index + 1)..]];

    for (var index = 0; index < value.Length; ++index)
      foreach (var smaller in Integer(value[index])) {
        var candidate = (byte[])value.Clone();
        candidate[index] = (byte)smaller;
        yield return candidate;
      }
  }

  private static IEnumerable<byte[]> Distinct(byte[] value, IEnumerable<byte[]> candidates) {
    var seen = new List<byte[]> { value };
    foreach (var candidate in candidates) {
      if (seen.Any(earlier => earlier.AsSpan().SequenceEqual(candidate)))
        continue;

      seen.Add(candidate);
      yield return candidate;
    }
  }

}
