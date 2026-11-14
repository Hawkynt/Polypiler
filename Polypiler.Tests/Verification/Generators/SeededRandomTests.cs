using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Core.Verification.Generators;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Verification.Generators;

/// <summary>SplitMix64 against the reference implementation, and the draws built on it.</summary>
[TestFixture]
public sealed class SeededRandomTests {

  /// <summary>Test design 1: every value of <c>splitmix64.json</c>, produced by Vigna's <c>splitmix64.c</c>.</summary>
  [Test]
  public void Outputs_AreThoseOfTheReferenceImplementation() {
    using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/fixtures/infra/property/splitmix64.json")));
    var compared = 0;
    foreach (var sequence in fixture.RootElement.GetProperty("sequences").EnumerateArray()) {
      var random = new SeededRandom(sequence.GetProperty("seed").GetUInt64());
      foreach (var output in sequence.GetProperty("outputs").EnumerateArray()) {
        Assert.That(random.NextUInt64(), Is.EqualTo(ulong.Parse(output.GetString()![2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)));
        ++compared;
      }
    }

    Assert.That(compared, Is.EqualTo(6));
  }

  /// <summary>Test design 5: 64 bits are the whole output, 1 bit its top bit.</summary>
  [Test]
  public void NextBits_IsTheTopOfTheOutput() {
    Assert.That(new SeededRandom(0).NextBits(64), Is.EqualTo(0xE220A8397B1DCDAFUL));
    Assert.That(new SeededRandom(0).NextBits(1), Is.EqualTo(1UL));
    Assert.That(new SeededRandom(0).NextBits(8), Is.EqualTo(0xE2UL));
  }

  /// <summary>Test design 4: widths outside 1…64 are refused.</summary>
  [TestCase(0)]
  [TestCase(65)]
  [TestCase(-1)]
  public void NextBits_RefusesWidthsOutsideTheRange(int width)
    => Assert.That(() => new SeededRandom(0).NextBits(width), Throws.TypeOf<ArgumentOutOfRangeException>().With.Property("ParamName").EqualTo("width"));

  /// <summary>Every value of a small range occurs, none outside it, and the same seed draws the same values.</summary>
  [TestCase(2)]
  [TestCase(5)]
  [TestCase(7)]
  [TestCase(8)]
  public void NextBelow_CoversExactlyTheRange(int exclusiveMax) {
    var random = new SeededRandom(2026);
    var drawn = Enumerable.Range(0, 1000).Select(_ => random.NextBelow(exclusiveMax)).ToList();
    Assert.That(drawn.Distinct().Order(), Is.EqualTo(Enumerable.Range(0, exclusiveMax)));
    var again = new SeededRandom(2026);
    Assert.That(Enumerable.Range(0, 1000).Select(_ => again.NextBelow(exclusiveMax)), Is.EqualTo(drawn));
  }

  /// <summary>The boundaries of the range: one value, the largest range, and no range at all.</summary>
  [Test]
  public void NextBelow_AtItsBoundaries() {
    var random = new SeededRandom(7);
    Assert.That(random.NextBelow(1), Is.Zero);
    Assert.That(Enumerable.Range(0, 100).Select(_ => random.NextBelow(int.MaxValue)), Has.All.InRange(0, int.MaxValue - 1));
    Assert.That(() => random.NextBelow(0), Throws.TypeOf<ArgumentOutOfRangeException>().With.Property("ParamName").EqualTo("exclusiveMax"));
    Assert.That(() => random.NextBelow(-5), Throws.TypeOf<ArgumentOutOfRangeException>());
  }

  /// <summary>Booleans are the top bit: both occur, and the first one of seed 0 is true.</summary>
  [Test]
  public void NextBoolean_IsTheTopBit() {
    Assert.That(new SeededRandom(0).NextBoolean(), Is.True);
    var random = new SeededRandom(2026);
    Assert.That(Enumerable.Range(0, 100).Select(_ => random.NextBoolean()).Distinct().Count(), Is.EqualTo(2));
  }

}
