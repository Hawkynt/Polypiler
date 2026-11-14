using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FsCheck.Fluent;
using NUnit.Framework;
using Polypiler.Core.Verification.Generators;
using Polypiler.Tests.Properties;

namespace Polypiler.Tests.Verification.Generators;

/// <summary>The property runner: inputs, shrinking, the failure record, the seed override and the FsCheck overload.</summary>
[TestFixture]
public sealed class PropertyRunnerTests {

  private string _directory = string.Empty;

  /// <summary>Gives each test its own directory to record into.</summary>
  [SetUp]
  public void CreateDirectory() => this._directory = Directory.CreateTempSubdirectory("polypiler-property-").FullName;

  /// <summary>Removes the directory again.</summary>
  [TearDown]
  public void DeleteDirectory() => Directory.Delete(this._directory, true);

  private PropertyRunner Runner(string? seedVariable = null)
    => new(this._directory, name => name == PropertyRunner.SeedVariable ? seedVariable : null);

  private static IEnumerable<ulong> Bytes(ulong seed) => BitPatterns.Sequence(8, seed);

  private PropertyFalsifiedException Falsify(Func<ulong, bool> predicate, string? seedVariable = null)
    => Assert.Throws<PropertyFalsifiedException>(() => this.Runner(seedVariable).ForAll(7, 1000, Bytes, Shrink.Integer, predicate, "Probe"))!;

  /// <summary>Test design 6: one input is exactly one evaluation.</summary>
  [Test]
  public void CountOfOne_EvaluatesExactlyOneInput() {
    var seen = new List<ulong>();
    Assert.That(this.Runner().ForAll(7, 1, Bytes, Shrink.Integer, x => { seen.Add(x); return true; }), Is.EqualTo(1));
    Assert.That(seen, Is.EqualTo(new ulong[] { 0 }));
  }

  /// <summary>Test design 6: no inputs, negative counts and missing delegates are refused.</summary>
  [Test]
  public void Arguments_OutsideTheirRanges_AreRefused() {
    var runner = this.Runner();
    Assert.That(() => runner.ForAll(7, -1, Bytes, Shrink.Integer, _ => true), Throws.TypeOf<ArgumentOutOfRangeException>().With.Property("ParamName").EqualTo("count"));
    Assert.That(() => runner.ForAll<ulong>(7, 1, null!, Shrink.Integer, _ => true), Throws.TypeOf<ArgumentNullException>().With.Property("ParamName").EqualTo("inputs"));
    Assert.That(() => runner.ForAll(7, 1, Bytes, null!, _ => true), Throws.TypeOf<ArgumentNullException>().With.Property("ParamName").EqualTo("shrink"));
    Assert.That(() => runner.ForAll(7, 1, Bytes, Shrink.Integer, null!), Throws.TypeOf<ArgumentNullException>().With.Property("ParamName").EqualTo("predicate"));
    Assert.That(() => runner.ForAll((ulong)7, 1, (FsCheck.Arbitrary<int>)null!, _ => true), Throws.TypeOf<ArgumentNullException>().With.Property("ParamName").EqualTo("arbitrary"));
  }

  /// <summary>A sequence shorter than the count ends the run when it ends.</summary>
  [Test]
  public void ShortSequence_EndsTheRun()
    => Assert.That(this.Runner().ForAll(7, 1000, _ => new ulong[] { 1, 2, 3 }, Shrink.Integer, _ => true), Is.EqualTo(3));

  /// <summary>Test design 12: a threshold property shrinks to exactly its threshold.</summary>
  [TestCase(100UL, "0x64", "0x7F")]
  [TestCase(1UL, "0x1", "0x1")]
  [TestCase(255UL, "0xFF", "0xFF")]
  public void ThresholdProperty_ShrinksToTheThreshold(ulong threshold, string shrunk, string original) {
    var failure = this.Falsify(x => x < threshold).Failure;
    Assert.That((failure.Shrunk, failure.Original), Is.EqualTo((shrunk, original)));
  }

  /// <summary>Test design 9: a property no value satisfies stops at the smallest candidate, 0, in one step.</summary>
  [Test]
  public void PropertyFailingEverywhere_StopsAtZeroInOneStep() {
    var failure = Assert.Throws<PropertyFalsifiedException>(() => this.Runner().ForAll(7, 10, _ => new ulong[] { 200 }, Shrink.Integer, _ => false))!.Failure;
    Assert.That((failure.Index, failure.Original, failure.Shrunk, failure.ShrinkSteps), Is.EqualTo((0, "0xC8", "0x0", 1)));
  }

  /// <summary>Test design 9: a shrinker that never runs out stops after the step limit.</summary>
  [Test]
  public void EndlessShrinking_StopsAtTheStepLimit() {
    var failure = Assert.Throws<PropertyFalsifiedException>(() => this.Runner().ForAll(7, 10, _ => new ulong[] { 0 }, x => [x + 1], _ => false))!.Failure;
    Assert.That((failure.Shrunk, failure.ShrinkSteps), Is.EqualTo(("0x3E8", PropertyRunner.MaxShrinkSteps)));
  }

  /// <summary>Test design 13: a buffer property shrinks to the one-byte buffer 00.</summary>
  [Test]
  public void BufferProperty_ShrinksToTheSmallestBuffer() {
    static IEnumerable<byte[]> Inputs(ulong seed) {
      var random = new SeededRandom(seed);
      while (true)
        yield return Buffers.Bytes(random, 64);
    }

    var failure = Assert.Throws<PropertyFalsifiedException>(() => this.Runner().ForAll(7, 1000, Inputs, Shrink.Buffer, buffer => !buffer.Contains((byte)0)))!.Failure;
    Assert.That(failure.Shrunk, Is.EqualTo("[00]"));
    Assert.That(failure.Original, Does.Contain("00"));
  }

  /// <summary>Test design 14: the FsCheck overload makes the same inputs from the same seed, different ones from another.</summary>
  [Test]
  public void ArbitraryOverload_RepeatsPerSeed() {
    var arbitrary = ArbMap.Default.ArbFor<int>();
    List<int> Run(ulong seed) {
      var seen = new List<int>();
      Assert.That(this.Runner().ForAll(seed, 100, arbitrary, value => { seen.Add(value); return true; }), Is.EqualTo(100));
      return seen;
    }

    var first = Run(7);
    Assert.That(Run(7), Is.EqualTo(first));
    Assert.That(Run(8), Is.Not.EqualTo(first));
    Assert.That(first.Distinct().Count(), Is.GreaterThan(10));
  }

  /// <summary>Test design 14: the FsCheck overload shrinks with the arbitrary's shrinker.</summary>
  [Test]
  public void ArbitraryOverload_ShrinksWithTheArbitrary() {
    var failure = Assert.Throws<PropertyFalsifiedException>(() => this.Runner().ForAll(7, 1000, ArbMap.Default.ArbFor<int>(), value => value < 10, "Arbitrary"))!.Failure;
    Assert.That(failure.Shrunk, Is.EqualTo("0xA"));
  }

  /// <summary>Test design 15: the record holds the documented fields and nothing for a seed nobody replaced.</summary>
  [Test]
  public void Record_HoldsTheDocumentedFields() {
    var failure = this.Falsify(x => x < 100).Failure;
    using var json = JsonDocument.Parse(File.ReadAllText(failure.RecordPath!));
    Assert.That(json.RootElement.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "test", "seed", "index", "original", "shrunk", "shrinkSteps" }));
    Assert.That(failure.RecordPath, Is.EqualTo(Path.Combine(this._directory, "Probe.json")));
  }

  /// <summary>Test design 16: a predicate that throws counts as falsified, and the exception type is recorded.</summary>
  [Test]
  public void ThrowingPredicate_IsFalsified() {
    var falsified = this.Falsify(x => 100 / (int)x > 0);
    Assert.That((falsified.Failure.Index, falsified.Failure.Shrunk, falsified.Failure.ShrinkSteps), Is.EqualTo((0, "0x0", 0)));
    Assert.That(falsified.Failure.Exception, Is.EqualTo("System.DivideByZeroException"));
    Assert.That(falsified.Message, Does.Contain("the predicate threw System.DivideByZeroException"));
    using var json = JsonDocument.Parse(File.ReadAllText(falsified.Failure.RecordPath!));
    Assert.That(json.RootElement.GetProperty("exception").GetString(), Is.EqualTo("System.DivideByZeroException"));
  }

  /// <summary>A predicate that throws only while shrinking hands its exception to the record.</summary>
  [Test]
  public void ExceptionWhileShrinking_IsTheOneRecorded() {
    var failure = this.Falsify(x => x == 0 ? throw new InvalidOperationException("zero") : x < 100).Failure;
    Assert.That((failure.Shrunk, failure.Exception), Is.EqualTo(("0x0", "System.InvalidOperationException")));
  }

  /// <summary>Test design 17: a malformed seed override names the variable.</summary>
  [TestCase("abc")]
  [TestCase("0x")]
  [TestCase("-1")]
  [TestCase("18446744073709551616")]
  [TestCase("0xG1")]
  public void MalformedSeedOverride_IsRefused(string text)
    => Assert.That(() => this.Runner(text).ForAll(7, 10, Bytes, Shrink.Integer, _ => true), Throws.TypeOf<FormatException>().With.Message.Contains(PropertyRunner.SeedVariable));

  /// <summary>Test design 19: a valid override replaces the declared seed, and the message and the record say so.</summary>
  [TestCase("0x10", 16UL)]
  [TestCase(" 16 ", 16UL)]
  [TestCase("0X10", 16UL)]
  [TestCase("18446744073709551615", ulong.MaxValue)]
  public void SeedOverride_ReplacesTheDeclaredSeed(string text, ulong seed) {
    var falsified = this.Falsify(x => x < 0x90, text);
    Assert.That(falsified.Failure.Seed, Is.EqualTo(seed));
    Assert.That(falsified.Message, Does.Contain($"(seed {seed})").And.Contain($"POLYPILER_PROPERTY_SEED={seed}").And.Contain("replaced the declared seed 7"));
    using var json = JsonDocument.Parse(File.ReadAllText(falsified.Failure.RecordPath!));
    Assert.That((json.RootElement.GetProperty("seed").GetUInt64(), json.RootElement.GetProperty("declaredSeed").GetUInt64()), Is.EqualTo((seed, 7UL)));
  }

  /// <summary>An empty override is no override.</summary>
  [TestCase("")]
  [TestCase("  ")]
  public void BlankSeedOverride_KeepsTheDeclaredSeed(string text)
    => Assert.That(this.Runner(text).EffectiveSeed(7), Is.EqualTo(7UL));

  /// <summary>The same override replays the same failure, whatever seed the test declared.</summary>
  [Test]
  public void SeedOverride_ReplaysTheSameInputs() {
    var seen = new List<ulong>[2];
    for (var run = 0; run < 2; ++run) {
      var list = seen[run] = [];
      this.Runner("12345").ForAll((ulong)run, 50, seed => BitPatterns.Sequence(16, seed), Shrink.Integer, x => { list.Add(x); return true; });
    }

    Assert.That(seen[1], Is.EqualTo(seen[0]));
  }

  /// <summary>Test design 18: a record that cannot be written still fails the property, and says so.</summary>
  [Test]
  public void UnwritableRecordDirectory_StillFails() {
    var blocked = Path.Combine(this._directory, "blocked");
    File.WriteAllText(blocked, "a file where the directory should be");
    var runner = new PropertyRunner(blocked, _ => null);
    var falsified = Assert.Throws<PropertyFalsifiedException>(() => runner.ForAll(7, 1000, Bytes, Shrink.Integer, x => x < 100, "Probe"))!;
    Assert.That(falsified.Message, Does.StartWith("PLY0071: ").And.Contain("the failure record was not written"));
    Assert.That(falsified.Failure.RecordPath, Is.Null);
  }

  /// <summary>Test names become file names: unsafe characters turn into underscores, a missing name into "property".</summary>
  [TestCase("A.B-c_1", "A.B-c_1.json")]
  [TestCase("a b/c:d", "a_b_c_d.json")]
  [TestCase("", "property.json")]
  public void RecordFileName_IsTheSafeTestName(string test, string file) {
    var failure = Assert.Throws<PropertyFalsifiedException>(() => this.Runner().ForAll(7, 1, _ => new ulong[] { 0 }, Shrink.Integer, _ => false, test))!.Failure;
    Assert.That(Path.GetFileName(failure.RecordPath), Is.EqualTo(file));
  }

  /// <summary>The process runner records into the repository's ignored <c>artifacts/properties/</c>.</summary>
  [Test]
  public void DefaultRunner_RecordsIntoTheRepositoryArtifacts()
    => Assert.That(PropertyRunner.Default.RecordDirectory, Is.EqualTo(Path.Combine(Polypiler.Prototype.Testing.PrototypeFixture.RepositoryRoot, "artifacts", "properties")));

  /// <summary>The static entry points pass through to the process runner.</summary>
  [Test]
  public void StaticEntryPoints_RunTheProperty() {
    var evaluated = 0;
    Property.ForAll(7, 20, Bytes, Shrink.Integer, _ => ++evaluated > 0);
    Property.ForAll(7, 20, ArbMap.Default.ArbFor<byte>(), _ => ++evaluated > 0);
    Assert.That(evaluated, Is.EqualTo(40));
  }

  /// <summary>Values as the record shows them.</summary>
  [Test]
  public void Format_ShowsEachKindOfValue() {
    Assert.That(PropertyRunner.Format(null), Is.EqualTo("null"));
    Assert.That(PropertyRunner.Format(new byte[] { 0x00, 0xAB }), Is.EqualTo("[00 AB]"));
    Assert.That(PropertyRunner.Format((byte)0xFF), Is.EqualTo("0xFF"));
    Assert.That(PropertyRunner.Format((sbyte)-1), Is.EqualTo("0xFF"));
    Assert.That(PropertyRunner.Format((ushort)0x1234), Is.EqualTo("0x1234"));
    Assert.That(PropertyRunner.Format((short)-2), Is.EqualTo("0xFFFE"));
    Assert.That(PropertyRunner.Format(0x80000000U), Is.EqualTo("0x80000000"));
    Assert.That(PropertyRunner.Format(-1), Is.EqualTo("0xFFFFFFFF"));
    Assert.That(PropertyRunner.Format(ulong.MaxValue), Is.EqualTo("0xFFFFFFFFFFFFFFFF"));
    Assert.That(PropertyRunner.Format(-1L), Is.EqualTo("0xFFFFFFFFFFFFFFFF"));
    Assert.That(PropertyRunner.Format(1.5f), Is.EqualTo("1.5 (0x3FC00000)"));
    Assert.That(PropertyRunner.Format(-0.0), Is.EqualTo("-0 (0x8000000000000000)"));
    Assert.That(PropertyRunner.Format((1UL, (byte)2)), Is.EqualTo("(0x1, 0x2)"));
    Assert.That(PropertyRunner.Format(1.25m), Is.EqualTo("1.25"));
    Assert.That(PropertyRunner.Format("text"), Is.EqualTo("text"));
  }

}
