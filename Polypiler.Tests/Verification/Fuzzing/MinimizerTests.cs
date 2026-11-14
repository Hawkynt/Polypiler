using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Fuzzing;
using Polypiler.Core.Verification.Generators;

namespace Polypiler.Tests.Verification.Fuzzing;

/// <summary>The <c>ddmin</c> minimizer: its results, 1-minimality and test design rows 2, 12 and 13.</summary>
[TestFixture]
public sealed class MinimizerTests {

  private static readonly byte[] _seed = [0x78, 0x78, 0x50, 0x4C, 0x59, 0x78, 0x78];

  private static bool ContainsPly(byte[] input) => input.AsSpan().IndexOf("PLY"u8) >= 0;

  private static bool StartsWithZero(byte[] input) => input.Length > 0 && input[0] == 0;

  /// <summary>Removing any single byte of the result makes the failure disappear.</summary>
  private static void AssertOneMinimal(byte[] result, Func<byte[], bool> fails) {
    Assert.That(fails(result), Is.True);
    for (var index = 0; index < result.Length; ++index)
      Assert.That(fails([.. result[..index], .. result[(index + 1)..]]), Is.False, $"without byte {index}");
  }

  /// <summary>Test design 2: the marker in its seed is reduced to the marker.</summary>
  [Test]
  public void TheMarker_IsMinimizedToItsThreeBytes() {
    var result = Minimizer.Minimize(_seed, ContainsPly);
    Assert.That(result, Is.EqualTo(new byte[] { 0x50, 0x4C, 0x59 }));
    AssertOneMinimal(result, ContainsPly);
  }

  [Test]
  public void TheInput_IsNotModified() {
    var input = (byte[])_seed.Clone();
    Minimizer.Minimize(input, ContainsPly);
    Assert.That(input, Is.EqualTo(_seed));
  }

  /// <summary>Test design 12: a 1-byte and an already minimal input stay as they are.</summary>
  [Test]
  public void AMinimalInput_StaysUnchanged() {
    Assert.That(Minimizer.Minimize([0x00], StartsWithZero), Is.EqualTo(new byte[] { 0x00 }));
    Assert.That(Minimizer.Minimize([0x50, 0x4C, 0x59], ContainsPly), Is.EqualTo(new byte[] { 0x50, 0x4C, 0x59 }));
  }

  /// <summary>Test design 13: when only the whole input fails, the whole input is the result.</summary>
  [Test]
  public void AFailureOfTheWholeInput_KeepsAllSevenBytes() {
    var asked = new List<byte[]>();
    var result = Minimizer.Minimize(_seed, candidate => {
      asked.Add(candidate);
      return candidate.AsSpan().SequenceEqual(_seed);
    });
    Assert.That(result, Is.EqualTo(_seed));
    Assert.That(asked, Is.Not.Empty);
    Assert.That(asked.Select(Convert.ToHexString), Is.Unique, "each distinct candidate is asked once");
  }

  [Test]
  public void AnInputThatFailsWhenEmpty_IsMinimizedToEmpty()
    => Assert.That(Minimizer.Minimize([1, 2, 3, 4, 5], _ => true), Is.Empty);

  [Test]
  public void TheEmptyInput_StaysEmpty()
    => Assert.That(Minimizer.Minimize([], _ => true), Is.Empty);

  [Test]
  public void BytesThatDoNotMatter_BecomeZero()
    => Assert.That(Minimizer.Minimize([7, 8, 9], candidate => candidate.Length == 3), Is.EqualTo(new byte[] { 0, 0, 0 }));

  /// <summary>Zeroing a byte can make another byte removable; the passes repeat until neither changes the input.</summary>
  [Test]
  public void ZeroingThatEnablesRemoval_IsFollowedByAnotherReduction() {
    // Fails when the sum of the bytes is 5 or more and the input holds an odd byte.
    static bool Fails(byte[] input) => input.Sum(value => value) >= 5 && input.Any(value => value % 2 == 1);
    var result = Minimizer.Minimize([3, 2, 4, 1], Fails);
    AssertOneMinimal(result, Fails);
  }

  [Test]
  public void RandomPredicates_GiveOneMinimalResults() {
    var random = new SeededRandom(11);
    for (var round = 0; round < 50; ++round) {
      var needle = Buffers.Bytes(random, 3);
      var input = Buffers.Bytes(random, 40);
      var at = random.NextBelow(input.Length + 1);
      input = [.. input[..at], .. needle, .. input[at..]];
      bool Fails(byte[] candidate) => candidate.AsSpan().IndexOf(needle) >= 0 && candidate.Length >= needle.Length;
      var result = Minimizer.Minimize(input, Fails);
      AssertOneMinimal(result, Fails);
      Assert.That(result.Length, Is.LessThanOrEqualTo(Math.Max(needle.Length, 1)));
    }
  }

  [Test]
  public void NullArguments_AreRefused() {
    Assert.That(() => Minimizer.Minimize(null!, _ => true), Throws.ArgumentNullException);
    Assert.That(() => Minimizer.Minimize([], null!), Throws.ArgumentNullException);
  }

}
