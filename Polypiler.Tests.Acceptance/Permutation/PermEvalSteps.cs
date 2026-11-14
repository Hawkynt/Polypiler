using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Permutation.Evaluation;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Semantics;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Permutation;

/// <summary>
/// The steps of <c>tests/features/permutation/perm-eval.feature</c>: mappings are evaluated in process
/// by <see cref="MappingEvaluator"/>, and the vectors BitPerm's raw evaluation exported under
/// <c>tests/fixtures/bitperm</c> are reproduced on their masks.
/// </summary>
[Binding, Scope(Feature = "Canonical mapping evaluator")]
public sealed class PermEvalSteps {

  private BitMapping? _mapping;
  private BitRouting? _routing;
  private BigInteger _result;
  private ulong[] _limbs = [];
  private Exception? _refusal;
  private string[] _names = [];
  private string _directory = "";
  private int _vectors;
  private int _mismatches;

  private BitMapping Mapping => this._mapping ?? throw new AssertionException("no mapping was given");

  private static BigInteger Hex(string text) {
    Assert.That(text, Does.StartWith("0x"));
    return BigInteger.Parse("0" + text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
  }

  /// <summary>Parses a mapping text.</summary>
  [Given("^the mapping " + StepText.Quoted + "$")]
  public void MappingText(string text) => this._mapping = BitMapping.Parse(text);

  /// <summary>A reversal of the given width.</summary>
  [Given(@"^BitMapping.Reverse\((\d+)\)$")]
  public void Reverse(int width) => this._mapping = BitMapping.Reverse(width);

  /// <summary>A routing of a mapping whose condition knows the given input bits to be 0.</summary>
  [Given("^the routing " + StepText.Quoted + " with condition InputKnownZero (0x[0-9A-Fa-f]+)$")]
  public void RoutingWithKnownZero(string text, string knownZero) {
    var mapping = BitMapping.Parse(text);
    this._routing = BitRouting.From(mapping, new BitCondition(Hex(knownZero), BigInteger.Zero, mapping.RequiredOutputMask));
  }

  /// <summary>Lists the exported mappings and checks every one has its vector file.</summary>
  [Given("^the (\\d+) mapping and vector files under " + StepText.Quoted + "$")]
  public void Fixtures(int count, string directory) {
    this._directory = Path.Combine(PrototypeFixture.RepositoryRoot, directory);
    this._names = [.. Directory.GetFiles(Path.Combine(this._directory, "mappings"), "*.json").Select(path => Path.GetFileNameWithoutExtension(path)!).Order(StringComparer.Ordinal)];
    var vectors = Directory.GetFiles(Path.Combine(this._directory, "vectors"), "*.json").Select(path => Path.GetFileNameWithoutExtension(path)!).Order(StringComparer.Ordinal);
    Assert.That(this._names, Has.Length.EqualTo(count));
    Assert.That(vectors, Is.EqualTo(this._names));
  }

  /// <summary>Evaluates the mapping on an input, keeping a refusal for a Then step.</summary>
  [When("^it is evaluated on (0x[0-9A-Fa-f]+)$")]
  public void EvaluateOn(string input) {
    try {
      this._result = MappingEvaluator.Evaluate(this.Mapping, Hex(input));
    } catch (ArgumentOutOfRangeException exception) {
      this._refusal = exception;
    }
  }

  /// <summary>Evaluates the mapping on the value 1 through the limb overload.</summary>
  [When(@"^it is evaluated on the value with only bit 0 set, as (\d+) limbs$")]
  public void EvaluateOnLimbs(int limbs) {
    var input = new ulong[limbs];
    input[0] = 1;
    this._limbs = new ulong[limbs];
    MappingEvaluator.Evaluate(this.Mapping, input, this._limbs);
  }

  /// <summary>Evaluates every vector of every fixture and counts the disagreements on the mask.</summary>
  [When("^each input is evaluated with MappingEvaluator$")]
  public void EvaluateFixtures() {
    foreach (var name in this._names) {
      BitMapping mapping;
      using (var stream = File.OpenRead(Path.Combine(this._directory, "mappings", name + ".json")))
        mapping = BitPermState.Read(stream);

      using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(this._directory, "vectors", name + ".json")));
      foreach (var vector in document.RootElement.GetProperty("vectors").EnumerateArray()) {
        var input = Hex(vector.GetProperty("input").GetString()!);
        var output = Hex(vector.GetProperty("output").GetString()!);
        var mask = Hex(vector.GetProperty("mask").GetString()!);
        ++this._vectors;
        if (!((MappingEvaluator.Evaluate(mapping, input) ^ output) & mask).IsZero)
          ++this._mismatches;
      }
    }
  }

  /// <summary>Evaluates the routing with its condition checked.</summary>
  [When("^EvaluateChecked is called with (0x[0-9A-Fa-f]+)$")]
  public void EvaluateChecked(string input) {
    var routing = this._routing ?? throw new AssertionException("no routing was given");
    try {
      this._result = MappingEvaluator.EvaluateChecked(routing, Hex(input));
    } catch (PermutationModelException exception) {
      this._refusal = exception;
    }
  }

  /// <summary>Checks the result.</summary>
  [Then("^the result is (0x[0-9A-Fa-f]+)$")]
  public void ResultIs(string expected) {
    Assert.That(this._refusal, Is.Null);
    Assert.That(this._result, Is.EqualTo(Hex(expected)));
  }

  /// <summary>Checks that every vector agreed.</summary>
  [Then("^every result equals the fixture output on the fixture mask$")]
  public void EveryResultAgrees() => Assert.That(this._vectors, Is.GreaterThan(this._names.Length), "every fixture has vectors");

  /// <summary>Checks the mismatch count.</summary>
  [Then(@"^the number of mismatches is (\d+)$")]
  public void MismatchesAre(int expected) => Assert.That(this._mismatches, Is.EqualTo(expected), $"{this._names.Length} mappings, {this._vectors} vectors");

  /// <summary>Checks that the limb result holds exactly one bit.</summary>
  [Then(@"^only bit (\d+) of the result is set$")]
  public void OnlyBit(int bit) {
    var expected = new ulong[this._limbs.Length];
    expected[bit / 64] = 1UL << (bit % 64);
    Assert.That(this._limbs, Is.EqualTo(expected));
  }

  /// <summary>Checks the refusal of an input.</summary>
  [Then("^an ArgumentOutOfRangeException with message " + StepText.Quoted + " is thrown$")]
  public void OutOfRange(string message) {
    Assert.That(this._refusal, Is.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(this._refusal!.Message, Is.EqualTo(new ArgumentOutOfRangeException("input", message).Message));
  }

  /// <summary>Checks the diagnostic of a refused input.</summary>
  [Then("^the diagnostic is " + StepText.Quoted + "$")]
  public void DiagnosticIs(string expected) {
    Assert.That(this._refusal, Is.TypeOf<PermutationModelException>());
    var diagnostic = ((PermutationModelException)this._refusal!).Diagnostic;
    Assert.That($"{diagnostic.Code}: {diagnostic.Message}", Is.EqualTo(expected));
  }

}
