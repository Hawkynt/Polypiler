using System;
using System.Collections.Generic;

namespace Polypiler.Core.Verification.Fuzzing;

/// <summary>
/// Reduces a failing input to a 1-minimal one: removing any single byte of the result makes the
/// failure disappear. The reduction is <c>ddmin</c> of Zeller and Hildebrandt, "Simplifying and
/// Isolating Failure-Inducing Input" (IEEE TSE 28(2), 2002) over bytes, followed by a pass that
/// replaces each remaining byte by <c>00</c> where the failure persists; the two repeat until neither
/// changes the input. Implemented from the paper; no code is taken.
/// </summary>
public static class Minimizer {

  /// <summary>Minimizes an input that fails.</summary>
  /// <param name="input">The failing input; it is not modified.</param>
  /// <param name="stillFails">Whether a candidate shows the same failure; each distinct candidate is asked once.</param>
  /// <returns>A 1-minimal input for which <paramref name="stillFails"/> holds, or the input itself when nothing smaller fails.</returns>
  public static byte[] Minimize(byte[] input, Func<byte[], bool> stillFails) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(stillFails);
    var asked = new Dictionary<string, bool>(StringComparer.Ordinal);
    bool Fails(byte[] candidate) {
      var key = Convert.ToHexString(candidate);
      if (!asked.TryGetValue(key, out var fails))
        asked[key] = fails = stillFails(candidate);
      return fails;
    }

    var current = (byte[])input.Clone();
    while (true) {
      current = DeltaDebug(current, Fails);
      if (!ZeroBytes(current, Fails))
        return current;
    }
  }

  /// <summary><c>ddmin</c>: tests chunks, then their complements, and doubles the granularity until each chunk is one byte.</summary>
  private static byte[] DeltaDebug(byte[] current, Func<byte[], bool> fails) {
    var granularity = 2;
    while (current.Length > 0) {
      granularity = Math.Min(granularity, current.Length);
      var reduced = Reduce(current, granularity, fails);
      if (reduced is { } smaller) {
        granularity = smaller.Granularity;
        current = smaller.Input;
        continue;
      }

      if (granularity == current.Length)
        break;

      granularity = Math.Min(current.Length, granularity * 2);
    }

    return current;
  }

  /// <summary>The first chunk, or else the first complement, that still fails, with the granularity to go on at.</summary>
  private static (byte[] Input, int Granularity)? Reduce(byte[] current, int granularity, Func<byte[], bool> fails) {
    var bounds = new int[granularity + 1];
    for (var chunk = 0; chunk <= granularity; ++chunk)
      bounds[chunk] = (int)((long)current.Length * chunk / granularity);

    // A single chunk is the whole input, which fails already.
    if (granularity > 1)
      for (var chunk = 0; chunk < granularity; ++chunk) {
        var subset = current[bounds[chunk]..bounds[chunk + 1]];
        if (fails(subset))
          return (subset, 2);
      }

    for (var chunk = 0; chunk < granularity; ++chunk) {
      byte[] complement = [.. current.AsSpan(0, bounds[chunk]), .. current.AsSpan(bounds[chunk + 1])];
      if (fails(complement))
        return (complement, Math.Max(granularity - 1, 2));
    }

    return null;
  }

  /// <summary>Replaces each non-zero byte by <c>00</c> where the failure persists; whether any was replaced.</summary>
  private static bool ZeroBytes(byte[] current, Func<byte[], bool> fails) {
    var changed = false;
    for (var index = 0; index < current.Length; ++index) {
      if (current[index] == 0)
        continue;

      var candidate = (byte[])current.Clone();
      candidate[index] = 0;
      if (!fails(candidate))
        continue;

      current[index] = 0;
      changed = true;
    }

    return changed;
  }

}
