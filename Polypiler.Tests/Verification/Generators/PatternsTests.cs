using System;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Polypiler.Core.Verification.Generators;

namespace Polypiler.Tests.Verification.Generators;

/// <summary>The integer and floating-point bit patterns and the buffers.</summary>
[TestFixture]
public sealed class PatternsTests {

  /// <summary>Test design 3: the edges of the narrowest widths, deduplicated, in order.</summary>
  [TestCase(1, new ulong[] { 0, 1 })]
  [TestCase(2, new ulong[] { 0, 1, 2, 3 })]
  [TestCase(3, new ulong[] { 0, 1, 3, 4, 6, 7 })]
  [TestCase(63, new ulong[] { 0, 1, 0x3FFFFFFFFFFFFFFF, 0x4000000000000000, 0x7FFFFFFFFFFFFFFE, 0x7FFFFFFFFFFFFFFF })]
  [TestCase(64, new ulong[] { 0, 1, 0x7FFFFFFFFFFFFFFF, 0x8000000000000000, 0xFFFFFFFFFFFFFFFE, 0xFFFFFFFFFFFFFFFF })]
  public void Edges_AreThoseOfTheWidth(int width, ulong[] edges)
    => Assert.That(BitPatterns.Edges(width), Is.EqualTo(edges));

  /// <summary>Test design 3: no edge and no random pattern of any width reaches 2^w.</summary>
  [Test]
  public void Patterns_StayWithinTheirWidth() {
    for (var width = BitPatterns.MinWidth; width <= BitPatterns.MaxWidth; ++width) {
      var mask = BitPatterns.Mask(width);
      Assert.That(BitPatterns.Sequence(width, 2026).Take(200).Where(value => (value & ~mask) != 0), Is.Empty, $"width {width}");
      Assert.That(BitPatterns.Sequence(width, 2026).Skip(BitPatterns.Edges(width).Count).Take(200).Distinct().Count(), Is.GreaterThan(1), $"width {width}");
    }
  }

  /// <summary>Test design 4: widths 0 and 65 are refused by every member that takes one.</summary>
  [TestCase(0)]
  [TestCase(65)]
  public void Widths_OutsideTheRange_AreRefused(int width) {
    Assert.That(() => BitPatterns.Edges(width), Throws.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(() => BitPatterns.Sequence(width, 7), Throws.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(() => BitPatterns.AsSigned(0, width), Throws.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(() => BitPatterns.Mask(width), Throws.TypeOf<ArgumentOutOfRangeException>());
  }

  /// <summary>Test design 11: the signed reading of a pattern is its two's-complement value.</summary>
  [TestCase(0x80UL, 8, -128L)]
  [TestCase(0x7FUL, 8, 127L)]
  [TestCase(0x1UL, 1, -1L)]
  [TestCase(0x0UL, 1, 0L)]
  [TestCase(0xFFFFUL, 16, -1L)]
  [TestCase(0x8000000000000000UL, 64, long.MinValue)]
  [TestCase(0x7FFFFFFFFFFFFFFFUL, 64, long.MaxValue)]
  public void AsSigned_IsTheTwosComplementReading(ulong value, int width, long expected) {
    Assert.That(BitPatterns.AsSigned(value, width), Is.EqualTo(expected));
    Assert.That(value & BitPatterns.Mask(width), Is.EqualTo(value), "the unsigned reading is the pattern itself");
  }

  /// <summary>
  /// PRD §23.3: the first 10 000 patterns of every width, hashed together, are the same on every
  /// operating system; the hash is printed so the CI runs can be compared, and pinned to the
  /// reference implementation's.
  /// </summary>
  [Test]
  public void Sequences_AreTheSameEverywhere() {
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    Span<byte> bytes = stackalloc byte[8];
    for (var width = BitPatterns.MinWidth; width <= BitPatterns.MaxWidth; ++width)
      foreach (var value in BitPatterns.Sequence(width, 7).Take(10_000)) {
        BitConverter.TryWriteBytes(bytes, value);
        if (!BitConverter.IsLittleEndian)
          bytes.Reverse();

        hash.AppendData(bytes);
      }

    var digest = Convert.ToHexString(hash.GetHashAndReset());
    TestContext.Out.WriteLine($"SHA-256 of the first 10000 patterns of widths 1…64, seed 7: {digest}");
    Assert.That(digest, Is.EqualTo(SequenceDigest));
  }

  // Produced independently by Vigna's splitmix64.c compiled with gcc: the edges of each width, then
  // the top w bits of the outputs from state 7, 10 000 values per width, each as 8 little-endian bytes.
  private const string SequenceDigest = "DAEF826D8A7921A62FF5082E2146EAB49FF6EF2031F1BB22523060DB49471764";

  /// <summary>Test design 10: one pattern per class of binary64 value, as listed.</summary>
  [Test]
  public void Edges64_AreOnePatternPerClass() {
    var values = FloatPatterns.Edges64.Select(BitConverter.UInt64BitsToDouble).ToList();
    Assert.That(FloatPatterns.Edges64, Has.Count.EqualTo(11));
    Assert.That(values[0], Is.EqualTo(0.0));
    Assert.That(double.IsNegative(values[1]) && values[1] == 0.0, Is.True);
    Assert.That(values[2], Is.EqualTo(double.Epsilon));
    Assert.That(double.IsSubnormal(values[3]) && !double.IsSubnormal(values[4]) && double.IsNormal(values[4]), Is.True);
    Assert.That(values[5], Is.EqualTo(1.0));
    Assert.That(values[6], Is.EqualTo(double.MaxValue));
    Assert.That(values[7], Is.EqualTo(double.PositiveInfinity));
    Assert.That(values[8], Is.EqualTo(double.NegativeInfinity));
    Assert.That(values.Skip(9), Has.All.NaN);
    Assert.That(FloatPatterns.Edges64[9] & 0x0008000000000000UL, Is.Not.Zero, "quiet");
    Assert.That(FloatPatterns.Edges64[10] & 0x0008000000000000UL, Is.Zero, "signalling");
  }

  /// <summary>Test design 10: one pattern per class of binary32 value, as listed.</summary>
  [Test]
  public void Edges32_AreOnePatternPerClass() {
    var values = FloatPatterns.Edges32.Select(BitConverter.UInt32BitsToSingle).ToList();
    Assert.That(FloatPatterns.Edges32, Has.Count.EqualTo(11));
    Assert.That(values[0], Is.EqualTo(0.0f));
    Assert.That(float.IsNegative(values[1]) && values[1] == 0.0f, Is.True);
    Assert.That(values[2], Is.EqualTo(float.Epsilon));
    Assert.That(float.IsSubnormal(values[3]) && float.IsNormal(values[4]), Is.True);
    Assert.That(values[5], Is.EqualTo(1.0f));
    Assert.That(values[6], Is.EqualTo(float.MaxValue));
    Assert.That(values[7], Is.EqualTo(float.PositiveInfinity));
    Assert.That(values[8], Is.EqualTo(float.NegativeInfinity));
    Assert.That(values.Skip(9), Has.All.NaN);
    Assert.That(FloatPatterns.Edges32[9] & 0x00400000U, Is.Not.Zero, "quiet");
    Assert.That(FloatPatterns.Edges32[10] & 0x00400000U, Is.Zero, "signalling");
  }

  /// <summary>The float sequences start with their edges and go on with the seed's patterns.</summary>
  [Test]
  public void FloatSequences_StartWithTheEdgesThenFollowTheSeed() {
    var random = new SeededRandom(7);
    Assert.That(FloatPatterns.Sequence64(7).Take(13), Is.EqualTo(FloatPatterns.Edges64.Append(random.NextUInt64()).Append(random.NextUInt64())));
    random = new SeededRandom(7);
    Assert.That(FloatPatterns.Sequence32(7).Take(13), Is.EqualTo(FloatPatterns.Edges32.Append((uint)(random.NextUInt64() >> 32)).Append((uint)(random.NextUInt64() >> 32))));
  }

  /// <summary>Buffers have every length up to the maximum, the empty one included, and the same seed gives the same buffers.</summary>
  [Test]
  public void Bytes_CoverEveryLengthAndRepeatPerSeed() {
    var random = new SeededRandom(2026);
    var buffers = Enumerable.Range(0, 200).Select(_ => Buffers.Bytes(random, 4)).ToList();
    Assert.That(buffers.Select(buffer => buffer.Length).Distinct().Order(), Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
    var again = new SeededRandom(2026);
    Assert.That(Enumerable.Range(0, 200).Select(_ => Buffers.Bytes(again, 4)), Is.EqualTo(buffers));
    Assert.That(Buffers.Bytes(new SeededRandom(1), 0), Is.Empty);
  }

  /// <summary>Shapes have lengths 0…max and strides 1…max.</summary>
  [Test]
  public void Shape_CoversLengthsAndStrides() {
    var random = new SeededRandom(2026);
    var shapes = Enumerable.Range(0, 200).Select(_ => Buffers.Shape(random, 3, 4)).ToList();
    Assert.That(shapes.Select(shape => shape.Length).Distinct().Order(), Is.EqualTo(new[] { 0, 1, 2, 3 }));
    Assert.That(shapes.Select(shape => shape.Stride).Distinct().Order(), Is.EqualTo(new[] { 1, 2, 3, 4 }));
    Assert.That(Buffers.Shape(new SeededRandom(1), 0, 1), Is.EqualTo((0, 1)));
  }

  /// <summary>Limits outside their ranges, and no generator, are refused.</summary>
  [Test]
  public void Buffers_RefuseLimitsOutsideTheirRanges() {
    var random = new SeededRandom(1);
    Assert.That(() => Buffers.Bytes(null!, 1), Throws.TypeOf<ArgumentNullException>());
    Assert.That(() => Buffers.Bytes(random, -1), Throws.TypeOf<ArgumentOutOfRangeException>().With.Property("ParamName").EqualTo("maxLength"));
    Assert.That(() => Buffers.Bytes(random, Array.MaxLength + 1), Throws.TypeOf<ArgumentOutOfRangeException>().With.Property("ParamName").EqualTo("maxLength"));
    Assert.That(() => Buffers.Shape(null!, 1, 1), Throws.TypeOf<ArgumentNullException>());
    Assert.That(() => Buffers.Shape(random, -1, 1), Throws.TypeOf<ArgumentOutOfRangeException>().With.Property("ParamName").EqualTo("maxLength"));
    Assert.That(() => Buffers.Shape(random, int.MaxValue, 1), Throws.TypeOf<ArgumentOutOfRangeException>().With.Property("ParamName").EqualTo("maxLength"));
    Assert.That(() => Buffers.Shape(random, 1, 0), Throws.TypeOf<ArgumentOutOfRangeException>().With.Property("ParamName").EqualTo("maxStride"));
  }

}
