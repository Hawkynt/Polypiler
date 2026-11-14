using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Verification.Generators;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Properties;

namespace Polypiler.Tests.Verification.Generators;

/// <summary>The scenarios of <c>tests/features/infra/infra-property.feature</c>.</summary>
[TestFixture]
public sealed class PropertyHarnessFeatureTests {

  private static readonly string[] _scenarios = [
    "A seed produces the published SplitMix64 sequence",
    "Integer sequences start with the edge values of their width",
    "A falsified property is shrunk to the smallest failing value and recorded",
    "A property that holds runs exactly the requested number of inputs",
    "A property without inputs to check is refused"
  ];

  private string _directory = string.Empty;

  /// <summary>Gives each test its own directory to record into.</summary>
  [SetUp]
  public void CreateDirectory() => this._directory = Directory.CreateTempSubdirectory("polypiler-property-").FullName;

  /// <summary>Removes the directory again.</summary>
  [TearDown]
  public void DeleteDirectory() => Directory.Delete(this._directory, true);

  private string RecordDirectory => Path.Combine(this._directory, "artifacts", "properties");

  private PropertyRunner Runner() => new(this.RecordDirectory, _ => null);

  /// <summary>Ensures the feature and its NUnit bindings stay in agreement.</summary>
  [Test, Category("Acceptance")]
  public void FeatureScenarios_HaveBindings() {
    var feature = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/features/infra/infra-property.feature"));
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario(?: Outline)?: (.+?)\r?$").Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
  }

  /// <summary>Scenario outline: A seed produces the published SplitMix64 sequence (test design 1).</summary>
  [TestCase("0", "0xE220A8397B1DCDAF", "0x6E789E6AA1B965F4", "0x06C45D188009454F")]
  [TestCase("1234567", "6457827717110365317", "3203168211198807973", "9817491932198370423")]
  [Category("Acceptance")]
  public void Seed_ProducesThePublishedSequence(string seed, string first, string second, string third) {
    var random = new SeededRandom(PropertyRunner.ParseSeed(seed));
    var drawn = new[] { random.NextUInt64(), random.NextUInt64(), random.NextUInt64() };
    Assert.That(drawn, Is.EqualTo(new[] { first, second, third }.Select(PropertyRunner.ParseSeed)));
  }

  /// <summary>Scenario outline: Integer sequences start with the edge values of their width.</summary>
  [TestCase(1, "0x0, 0x1")]
  [TestCase(8, "0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF")]
  [TestCase(16, "0x0000, 0x0001, 0x7FFF, 0x8000, 0xFFFE, 0xFFFF")]
  [TestCase(64, "0x0, 0x1, 0x7FFFFFFFFFFFFFFF, 0x8000000000000000, 0xFFFFFFFFFFFFFFFE, 0xFFFFFFFFFFFFFFFF")]
  [Category("Acceptance")]
  public void IntegerSequence_StartsWithTheEdges(int width, string edges) {
    var expected = edges.Split(", ").Select(PropertyRunner.ParseSeed).ToList();
    Assert.That(BitPatterns.Sequence(width, 7).Take(expected.Count), Is.EqualTo(expected));
  }

  /// <summary>Scenario: A falsified property is shrunk to the smallest failing value and recorded (test design 12, 15).</summary>
  [Test, Category("Acceptance")]
  public void FalsifiedProperty_IsShrunkAndRecorded() {
    var falsified = Assert.Throws<PropertyFalsifiedException>(() => this.Runner().ForAll(7, 1000, seed => BitPatterns.Sequence(8, seed), Shrink.Integer, x => x < 100, "LessThan100"))!;
    Assert.That(falsified.Message, Does.StartWith("PLY0071: property 'LessThan100' falsified after 3 inputs (seed 7): 0x64 (original 0x7F, 5 shrink steps); replay with POLYPILER_PROPERTY_SEED=7"));
    Assert.That(falsified.Failure.Index, Is.EqualTo(2));

    var record = Directory.GetFiles(this.RecordDirectory).Single();
    using var json = JsonDocument.Parse(File.ReadAllText(record));
    Assert.That(Path.GetFileName(record), Is.EqualTo("LessThan100.json"));
    Assert.That(json.RootElement.GetProperty("test").GetString(), Is.EqualTo("LessThan100"));
    Assert.That(json.RootElement.GetProperty("seed").GetUInt64(), Is.EqualTo(7));
    Assert.That(json.RootElement.GetProperty("index").GetInt32(), Is.EqualTo(2));
    Assert.That(json.RootElement.GetProperty("shrunk").GetString(), Is.EqualTo("0x64"));
    Assert.That(json.RootElement.GetProperty("original").GetString(), Is.EqualTo("0x7F"));
    Assert.That(json.RootElement.GetProperty("shrinkSteps").GetInt32(), Is.EqualTo(5));
    Assert.That(falsified.Message, Does.Contain(record));
  }

  /// <summary>Scenario: A property that holds runs exactly the requested number of inputs (test design 2).</summary>
  [Test, Category("Acceptance")]
  public void HoldingProperty_RunsExactlyTheRequestedInputs() {
    var evaluated = 0;
    var held = this.Runner().ForAll(7, 1000, seed => BitPatterns.Sequence(32, seed), Shrink.Integer, x => {
      ++evaluated;
      return (x ^ x) == 0;
    });
    Assert.That((held, evaluated), Is.EqualTo((1000, 1000)));
    Assert.That(Directory.Exists(this.RecordDirectory), Is.False);
  }

  /// <summary>Scenario: A property without inputs to check is refused (test design 6).</summary>
  [Test, Category("Acceptance")]
  public void PropertyWithoutInputs_IsRefused() {
    var refused = Assert.Throws<ArgumentOutOfRangeException>(() => this.Runner().ForAll(7, 0, seed => BitPatterns.Sequence(8, seed), Shrink.Integer, x => x.Equals(x)))!;
    Assert.That(refused.ParamName, Is.EqualTo("count"));
    Assert.That(Directory.Exists(this.RecordDirectory), Is.False);
  }

  /// <summary>The worked values of the issue: the shrink path from 0x7F, candidate by candidate.</summary>
  [Test]
  public void WorkedExample_ShrinksAlongThePublishedPath() {
    ulong[] path = [127, 112, 105, 102, 101, 100];
    for (var step = 0; step + 1 < path.Length; ++step)
      Assert.That(Shrink.Integer(path[step]).First(candidate => candidate >= 100), Is.EqualTo(path[step + 1]), path[step].ToString(CultureInfo.InvariantCulture));

    Assert.That(Shrink.Integer(100), Is.EqualTo(new ulong[] { 0, 50, 75, 88, 94, 97, 99 }));
  }

}
