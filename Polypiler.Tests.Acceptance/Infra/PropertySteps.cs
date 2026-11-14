using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Verification.Generators;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Properties;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/infra-property.feature</c>: the SplitMix64 sequence, the edge
/// values of the bit patterns and the property runner, which records into the scenario's workspace.
/// </summary>
[Binding, Scope(Feature = "Property-based test harness with fixed seeds and shrinking")]
public sealed partial class PropertySteps(ScenarioWorkspace workspace) {

  /// <summary>
  /// The properties the scenarios name, with the name each is recorded under. Their text is the
  /// feature's; what it means is the predicate beside it.
  /// </summary>
  private static readonly Dictionary<string, (string Name, Func<ulong, bool> Predicate)> _properties = new(StringComparer.Ordinal) {
    ["x < 100"] = ("LessThan100", x => x < 100),
    ["(x ^ x) == 0"] = ("XorSelfIsZero", x => (x ^ x) == 0),
    ["x == x"] = ("EqualsItself", x => x.Equals(x))
  };

  /// <summary>The shrink path of the worked example from 0x7F: 0x70, 0x69, 0x66, 0x65 and 0x64.</summary>
  private const int ShrinkStepsFrom0x7F = 5;

  private SeededRandom? _random;
  private IReadOnlyList<ulong> _values = [];
  private IEnumerable<ulong> _sequence = [];
  private (string Name, Func<ulong, bool> Predicate) _property;
  private int _width;
  private ulong _seed;
  private int _count;
  private int _evaluated;
  private int _held;
  private Exception? _thrown;
  private int _index;

  [GeneratedRegex(", | and ")]
  private static partial Regex ListSeparator();

  private string RecordDirectory => workspace.Resolve("artifacts/properties");

  /// <summary>Creates the generator.</summary>
  [Given(@"^a seeded random generator with seed (\S+)$")]
  public void SeededGenerator(string seed) => this._random = new SeededRandom(PropertyRunner.ParseSeed(seed));

  /// <summary>Draws three values.</summary>
  [When("^three 64-bit values are drawn$")]
  public void ThreeValuesDrawn() => this._values = [this._random!.NextUInt64(), this._random.NextUInt64(), this._random.NextUInt64()];

  /// <summary>Takes the bit-pattern sequence of a width.</summary>
  [Given(@"^the bit-pattern sequence for width (\d+) with seed (\S+)$")]
  public void BitPatternSequence(int width, string seed) => this._sequence = BitPatterns.Sequence(width, PropertyRunner.ParseSeed(seed));

  /// <summary>Takes as many values of the sequence as the step that checks them lists.</summary>
  [When("^the first values are taken$")]
  public void FirstValuesTaken() => this._values = [.. this._sequence.Take(64)];

  /// <summary>Checks the values drawn or taken, in decimal or <c>0x</c> hexadecimal, separated by commas and a final "and".</summary>
  [Then("^they are (.+)$")]
  public void TheyAre(string list) {
    var expected = ListSeparator().Split(list).Select(PropertyRunner.ParseSeed).ToList();
    Assert.That(this._values.Take(expected.Count), Is.EqualTo(expected));
    Assert.That(this._values, Has.Count.GreaterThanOrEqualTo(expected.Count));
  }

  /// <summary>Takes a property, the width of its bit patterns, the seed and the number of inputs.</summary>
  [Given("^the property " + StepText.Quoted + @" over (\d+)-bit patterns with seed (\S+) and (\d+) inputs$")]
  public void Property(string property, int width, string seed, int count) {
    this._property = _properties[property];
    this._width = width;
    this._seed = PropertyRunner.ParseSeed(seed);
    this._count = count;
  }

  /// <summary>Runs the property and keeps what it returned or threw.</summary>
  [When("^the property is checked$")]
  public void PropertyIsChecked() {
    var runner = new PropertyRunner(this.RecordDirectory, _ => null);
    var (name, predicate) = this._property;
    var width = this._width;
    try {
      this._held = runner.ForAll(this._seed, this._count, seed => BitPatterns.Sequence(width, seed), Shrink.Integer, x => {
        ++this._evaluated;
        return predicate(x);
      }, name);
    } catch (Exception exception) when (exception is PropertyFalsifiedException or ArgumentOutOfRangeException) {
      this._thrown = exception;
    }
  }

  /// <summary>Checks the failure: its code, the shrunk and the original value, the seed and the index.</summary>
  [Then(@"^it fails with PLY0071 naming the shrunk value (\S+) and the original value (\S+) at input index (\d+)$")]
  public void FailsWith(string shrunk, string original, int index) {
    Assert.That(this._thrown, Is.InstanceOf<PropertyFalsifiedException>());
    var falsified = (PropertyFalsifiedException)this._thrown!;
    Assert.That(falsified.Message, Does.StartWith(
      $"PLY0071: property '{this._property.Name}' falsified after {index + 1} inputs (seed {this._seed}): {shrunk} (original {original}, {ShrinkStepsFrom0x7F} shrink steps); replay with POLYPILER_PROPERTY_SEED={this._seed}"));
    Assert.That(falsified.Failure.Index, Is.EqualTo(index));
    this._index = index;
  }

  /// <summary>Checks the one record written: its file name, the test, the seed, the index and the values.</summary>
  [Then("^" + StepText.Quoted + @" contains a record with seed (\S+), shrunk " + StepText.Quoted + " and original " + StepText.Quoted + "$")]
  public void ContainsARecord(string directory, string seed, string shrunk, string original) {
    Assert.That(workspace.Resolve(directory).TrimEnd(Path.DirectorySeparatorChar), Is.EqualTo(this.RecordDirectory));
    var record = Directory.GetFiles(this.RecordDirectory).Single();
    using var json = JsonDocument.Parse(File.ReadAllText(record));
    Assert.That(Path.GetFileName(record), Is.EqualTo(this._property.Name + ".json"));
    Assert.That(json.RootElement.GetProperty("test").GetString(), Is.EqualTo(this._property.Name));
    Assert.That(json.RootElement.GetProperty("seed").GetUInt64(), Is.EqualTo(PropertyRunner.ParseSeed(seed)));
    Assert.That(json.RootElement.GetProperty("index").GetInt32(), Is.EqualTo(this._index));
    Assert.That(json.RootElement.GetProperty("shrunk").GetString(), Is.EqualTo(shrunk));
    Assert.That(json.RootElement.GetProperty("original").GetString(), Is.EqualTo(original));
    Assert.That(json.RootElement.GetProperty("shrinkSteps").GetInt32(), Is.EqualTo(ShrinkStepsFrom0x7F));
    Assert.That(this._thrown!.Message, Does.Contain(record));
  }

  /// <summary>Checks that the property held for every input and was evaluated once per input.</summary>
  [Then(@"^it passes after (\d+) inputs$")]
  public void PassesAfter(int count) {
    Assert.That(this._thrown, Is.Null);
    Assert.That((this._held, this._evaluated), Is.EqualTo((count, count)));
  }

  /// <summary>Checks that nothing was recorded.</summary>
  [Then("^no failure record is written$")]
  public void NoRecord() => Assert.That(Directory.Exists(this.RecordDirectory), Is.False);

  /// <summary>Checks the refusal, and that the refused property recorded nothing.</summary>
  [Then("^an ArgumentOutOfRangeException names the parameter " + StepText.Quoted + "$")]
  public void Refused(string parameter) {
    Assert.That(this._thrown, Is.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(((ArgumentOutOfRangeException)this._thrown!).ParamName, Is.EqualTo(parameter));
    Assert.That(this._evaluated, Is.Zero);
    Assert.That(Directory.Exists(this.RecordDirectory), Is.False);
  }

}
