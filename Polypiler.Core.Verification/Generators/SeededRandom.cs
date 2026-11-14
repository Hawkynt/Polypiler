using System;

namespace Polypiler.Core.Verification.Generators;

/// <summary>
/// SplitMix64, the generator of Sebastiano Vigna's public-domain <c>splitmix64.c</c> with its
/// constants copied exactly: a seed means the same values on every platform and in every runner,
/// because the state and every step are plain 64-bit integer arithmetic.
/// </summary>
public sealed class SeededRandom {

  private const ulong Golden = 0x9E3779B97F4A7C15UL;
  private const ulong FirstMix = 0xBF58476D1CE4E5B9UL;
  private const ulong SecondMix = 0x94D049BB133111EBUL;

  private ulong _state;

  /// <summary>Starts the generator; the state is the seed itself, as in the reference.</summary>
  public SeededRandom(ulong seed) => this._state = seed;

  /// <summary>The next 64-bit output.</summary>
  public ulong NextUInt64() {
    var z = this._state += Golden;
    z = (z ^ (z >> 30)) * FirstMix;
    z = (z ^ (z >> 27)) * SecondMix;
    return z ^ (z >> 31);
  }

  /// <summary>The top <paramref name="width"/> bits of the next output, as an unsigned pattern of that width.</summary>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is outside 1…64.</exception>
  public ulong NextBits(int width) {
    BitPatterns.CheckWidth(width);
    return this.NextUInt64() >> (64 - width);
  }

  /// <summary>A value in <c>[0, exclusiveMax)</c>, without the bias of a plain remainder.</summary>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="exclusiveMax"/> is below 1.</exception>
  public int NextBelow(int exclusiveMax) {
    ArgumentOutOfRangeException.ThrowIfLessThan(exclusiveMax, 1);
    if (exclusiveMax == 1)
      return 0;

    // Draw just enough bits to cover the range and reject what lies beyond it: every value of the
    // range keeps the same chance, and fewer than half the draws are thrown away.
    var width = 32 - int.LeadingZeroCount(exclusiveMax - 1);
    while (true) {
      var candidate = (int)this.NextBits(width);
      if (candidate < exclusiveMax)
        return candidate;
    }
  }

  /// <summary>A boolean from the top bit of the next output.</summary>
  public bool NextBoolean() => this.NextBits(1) == 1;

}
