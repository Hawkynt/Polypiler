using System;
using System.Collections.Generic;
using Polypiler.Core.Verification.Generators;

namespace Polypiler.Core.Verification.Fuzzing;

/// <summary>Changes an input into a new one; draws every choice from the generator given.</summary>
/// <param name="input">The input to change; it is not modified.</param>
/// <param name="random">The source of every choice.</param>
/// <param name="corpus">The corpus, for mutators that combine entries.</param>
/// <returns>A new array.</returns>
public delegate byte[] Mutator(byte[] input, SeededRandom random, IReadOnlyList<byte[]> corpus);

/// <summary>
/// The mutators of the in-process engine. Each returns a new array and draws only from the
/// <see cref="SeededRandom"/> it is given, so an input depends only on the seed, the iteration and the
/// corpus. A mutator that needs a byte to work on returns a copy of an empty input unchanged.
/// </summary>
public static class Mutators {

  /// <summary>The values <see cref="ReplaceInteresting"/> chooses from: <c>00 01 7F 80 FF 0A 20 22 5C</c>.</summary>
  public static IReadOnlyList<byte> InterestingValues { get; } = [0x00, 0x01, 0x7F, 0x80, 0xFF, 0x0A, 0x20, 0x22, 0x5C];

  /// <summary>Every mutator, in the order <see cref="Mutate"/> numbers them.</summary>
  public static IReadOnlyList<Mutator> All { get; } = [FlipBit, ReplaceInteresting, InsertByte, DeleteByte, DuplicateChunk, Splice, Truncate];

  /// <summary>Applies one to four mutators, each chosen from <see cref="All"/>.</summary>
  public static byte[] Mutate(byte[] input, SeededRandom random, IReadOnlyList<byte[]> corpus) {
    ArgumentNullException.ThrowIfNull(random);
    var count = 1 + random.NextBelow(4);
    for (var step = 0; step < count; ++step)
      input = All[random.NextBelow(All.Count)](input, random, corpus);

    return input;
  }

  /// <summary>Inverts one bit: same length, exactly one bit differs.</summary>
  public static byte[] FlipBit(byte[] input, SeededRandom random, IReadOnlyList<byte[]> corpus) {
    var output = Copy(input, random);
    if (output.Length > 0)
      output[random.NextBelow(output.Length)] ^= (byte)(1 << random.NextBelow(8));
    return output;
  }

  /// <summary>Replaces one byte by one of <see cref="InterestingValues"/>: same length, at most one byte differs.</summary>
  public static byte[] ReplaceInteresting(byte[] input, SeededRandom random, IReadOnlyList<byte[]> corpus) {
    var output = Copy(input, random);
    if (output.Length > 0)
      output[random.NextBelow(output.Length)] = InterestingValues[random.NextBelow(InterestingValues.Count)];
    return output;
  }

  /// <summary>Inserts one random byte anywhere, the end included: one byte longer.</summary>
  public static byte[] InsertByte(byte[] input, SeededRandom random, IReadOnlyList<byte[]> corpus) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(random);
    var position = random.NextBelow(input.Length + 1);
    return [.. input.AsSpan(0, position), (byte)random.NextBits(8), .. input.AsSpan(position)];
  }

  /// <summary>Removes one byte: one byte shorter.</summary>
  public static byte[] DeleteByte(byte[] input, SeededRandom random, IReadOnlyList<byte[]> corpus) {
    var output = Copy(input, random);
    if (output.Length == 0)
      return output;

    var position = random.NextBelow(output.Length);
    return [.. output.AsSpan(0, position), .. output.AsSpan(position + 1)];
  }

  /// <summary>Repeats a chunk of one or more bytes right after itself: longer by the chunk's length.</summary>
  public static byte[] DuplicateChunk(byte[] input, SeededRandom random, IReadOnlyList<byte[]> corpus) {
    var output = Copy(input, random);
    if (output.Length == 0)
      return output;

    var start = random.NextBelow(output.Length);
    var end = start + 1 + random.NextBelow(output.Length - start);
    return [.. output.AsSpan(0, end), .. output.AsSpan(start, end - start), .. output.AsSpan(end)];
  }

  /// <summary>Joins a prefix of the input with a suffix of a corpus entry; a copy without a corpus.</summary>
  public static byte[] Splice(byte[] input, SeededRandom random, IReadOnlyList<byte[]> corpus) {
    var output = Copy(input, random);
    ArgumentNullException.ThrowIfNull(corpus);
    if (corpus.Count == 0)
      return output;

    var other = corpus[random.NextBelow(corpus.Count)];
    var cut = random.NextBelow(output.Length + 1);
    return [.. output.AsSpan(0, cut), .. other.AsSpan(random.NextBelow(other.Length + 1))];
  }

  /// <summary>Cuts the input to a shorter length, possibly empty.</summary>
  public static byte[] Truncate(byte[] input, SeededRandom random, IReadOnlyList<byte[]> corpus) {
    var output = Copy(input, random);
    return output.Length == 0 ? output : output[..random.NextBelow(output.Length)];
  }

  private static byte[] Copy(byte[] input, SeededRandom random) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(random);
    return (byte[])input.Clone();
  }

}
