using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/ver-manifest.feature</c>. A manifest is made from the valid
/// fixtures of <c>tests/fixtures/infra/manifest/</c>, written into the scenario's workspace by
/// <see cref="ManifestWriter"/> and read back as JSON; a fixture is validated where it lies.
/// </summary>
[Binding, Scope(Feature = "Generated manifest schema, writer and validator")]
public sealed class VerManifestSteps(ScenarioWorkspace workspace) {

  private const string Fixtures = "tests/fixtures/infra/manifest";

  private static readonly ManifestExactness _exact = new("accurate", "exact", "signed-zero", [], [], new(3, 0, 0, null));

  private Manifest _manifest = Fixture("generated.json");
  private string _text = "";
  private string[] _serializations = [];
  private string _file = "";
  private readonly DiagnosticBag _diagnostics = new();

  private static Manifest Fixture(string name)
    => ManifestReader.Read(Path.Combine(PrototypeFixture.RepositoryRoot, Fixtures, name), new DiagnosticBag())
      ?? throw new InvalidOperationException($"the fixture {name} is not a valid manifest");

  private static int? Number(string value) => value == "none" ? null : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

  [Given(@"^a manifest with build exit code (\w+), execution exit code (\w+) and verification (\w+) of (\w+) passed$")]
  public void ManifestWithEvidence(string build, string run, string passed, string total) {
    Assert.That(passed == "none", Is.EqualTo(total == "none"), "passed and total are given together");
    this._manifest = this._manifest with {
      Build = Number(build) is { } buildExit ? new("node", "node --check Waves.mjs", buildExit) : null,
      Execution = Number(run) is { } runExit ? new("node Waves.test.mjs", runExit) : null,
      Verification = Number(total) is { } count ? new("dotnet-sdk", "dotnet-sdk", _exact, count, Number(passed)!.Value, count - Number(passed)!.Value) : null
    };
  }

  [Given("^an input file with the (\\d+)-byte content " + StepText.Quoted + " taken from " + StepText.Quoted + " at commit " + StepText.Quoted + "$")]
  public void PinnedInput(int length, string content, string repository, string commit) {
    var path = workspace.Write("abc.txt", content);
    var bytes = File.ReadAllBytes(path);
    Assert.That(bytes, Has.Length.EqualTo(length));
    this._manifest = this._manifest with { Inputs = [new("../abc.txt", ManifestWriter.Sha256(bytes), new(repository, commit, "abc.txt"))] };
  }

  [Given("^the manifest of fixture " + StepText.Quoted + " with its " + StepText.Quoted + " list in reverse order$")]
  public void ReversedFixture(string fixture, string list) {
    Assert.That(list, Is.EqualTo("outputs"));
    var manifest = Fixture(fixture);
    this._manifest = manifest with { Outputs = [.. manifest.Outputs.Reverse()] };
  }

  [Given("^the file " + StepText.Quoted + "$")]
  public void RepositoryFile(string path) {
    this._file = Path.Combine(PrototypeFixture.RepositoryRoot, path);
    Assert.That(File.Exists(this._file), Is.True, path);
  }

  [Given("^the file " + StepText.Quoted + " whose " + StepText.Quoted + " is " + StepText.Quoted + "$")]
  public void RepositoryFileWith(string path, string property, string value) {
    this.RepositoryFile(path);
    Assert.That(JsonNode.Parse(File.ReadAllText(this._file))![property]!.GetValue<string>(), Is.EqualTo(value));
  }

  [When("^the manifest is written$")]
  public void Written() {
    var directory = workspace.Resolve("out");
    Directory.CreateDirectory(directory);
    ManifestWriter.Write(this._manifest, directory);
    this._text = File.ReadAllText(Path.Combine(directory, Manifest.FileName));
  }

  [When("^it is serialized twice$")]
  public void SerializedTwice() {
    this._serializations = [ManifestWriter.Serialize(this._manifest), ManifestWriter.Serialize(this._manifest)];
    this._text = this._serializations[0];
  }

  [When("^it is validated$")]
  public void Validated() => ManifestValidator.Validate(this._file, this._diagnostics);

  [Then("^its " + StepText.Quoted + " is " + StepText.Quoted + "$")]
  public void PropertyOfTheManifest(string property, string value) => this.PathIs(property, value);

  [Then("^" + StepText.Quoted + " is " + StepText.Quoted + "$")]
  public void PathIs(string path, string value) {
    JsonNode? node = JsonNode.Parse(this._text);
    foreach (Match step in Regex.Matches(path, @"(\w+)|\[(\d+)\]"))
      node = step.Groups[1].Success ? node![step.Groups[1].Value] : node![int.Parse(step.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)];

    Assert.That(node!.GetValue<string>(), Is.EqualTo(value));
  }

  [Then("^both serializations are byte-identical$")]
  public void ByteIdentical()
    => Assert.That(Encoding.UTF8.GetBytes(this._serializations[0]), Is.EqualTo(Encoding.UTF8.GetBytes(this._serializations[1])));

  [Then("^" + StepText.Quoted + " is sorted by " + StepText.Quoted + "$")]
  public void SortedBy(string list, string key) {
    var values = JsonNode.Parse(this._text)![list]!.AsArray().Select(item => item![key]!.GetValue<string>()).ToList();
    Assert.That(values, Is.EqualTo(values.Order(StringComparer.Ordinal).ToList()));
    Assert.That(values, Has.Count.GreaterThan(1));
  }

  [Then("^the text contains no absolute path and no timestamp$")]
  public void NoAbsolutePathNoTimestamp() {
    Assert.That(Regex.IsMatch(this._text, "\"(/|[A-Za-z]:[\\\\/]|~)"), Is.False, "absolute path");
    Assert.That(Regex.IsMatch(this._text, @"\d{4}-\d{2}-\d{2}|\d{2}:\d{2}:\d{2}"), Is.False, "timestamp");
  }

  [Then("^diagnostic (PLY\\d{4}) is reported with " + StepText.Quoted + "$")]
  public void ReportedWith(string code, string text)
    => Assert.That(this._diagnostics.Items.Where(diagnostic => diagnostic.Code == code && diagnostic.Message.Contains(text, StringComparison.Ordinal)), Is.Not.Empty,
      string.Join(Environment.NewLine, this._diagnostics.Items));

  [Then(@"^the mapped exit code is (\d+)$")]
  public void MappedExitCode(int exitCode) => Assert.That((int)this._diagnostics.ExitCode, Is.EqualTo(exitCode));

  [Then("^diagnostic (PLY\\d{4}) is reported at JSON pointer " + StepText.Quoted + "$")]
  public void ReportedAt(string code, string pointer)
    => Assert.That(this._diagnostics.Items.Where(diagnostic => diagnostic.Code == code && diagnostic.Message.Contains($" is invalid: {pointer}: ", StringComparison.Ordinal)), Is.Not.Empty,
      string.Join(Environment.NewLine, this._diagnostics.Items));

  private Manifest Written(string path) {
    var bag = new DiagnosticBag();
    var manifest = ManifestReader.Read(workspace.Resolve(path), bag);
    Assert.That(bag.Items, Is.Empty);
    return manifest!;
  }

  [Then("^the manifest " + StepText.Quoted + " is valid with state " + StepText.Quoted + "$")]
  public void ValidWithState(string path, string state) => Assert.That(ManifestStateRule.Name(this.Written(path).State), Is.EqualTo(state));

  [Then("^the manifest " + StepText.Quoted + " records " + StepText.Quoted + " with its SHA-256 and " + StepText.Quoted + " as " + StepText.Quoted + "$")]
  public void RecordsOutputAndInput(string path, string output, string input, string recorded) {
    var manifest = this.Written(path);
    Assert.That(manifest.Outputs.Single(), Is.EqualTo(new ManifestOutput(Path.GetFileName(output), ManifestWriter.Sha256(File.ReadAllBytes(workspace.Resolve(output))), ManifestOutputRole.Source)));
    Assert.That(manifest.Inputs.Single(), Is.EqualTo(new ManifestInput(recorded, ManifestWriter.Sha256(File.ReadAllBytes(workspace.Resolve(input))), null)));
  }

  [Then("^the manifest " + StepText.Quoted + " lists the native functions the header of " + StepText.Quoted + " lists$")]
  public void NativesOfTheHeader(string path, string generated) {
    var header = new List<string>();
    foreach (var line in File.ReadLines(workspace.Resolve(generated)).TakeWhile(line => line.StartsWith("//", StringComparison.Ordinal)))
      if (Regex.Match(line, @"^//\s+(\w+) of (f32|f64): (\S+), slack (\d+) on (\S+), bound (\d+)$") is { Success: true } match)
        header.Add($"INTRINSICS.{match.Groups[1].Value} {match.Groups[2].Value} {match.Groups[3].Value} {match.Groups[4].Value} {match.Groups[5].Value} {match.Groups[6].Value}");

    var floating = this.Written(path).FloatingPoint!;
    Assert.That(floating.Natives.Select(native => $"{native.Operation} {native.Format} {native.Accuracy} {native.Slack} {native.Runtime} {native.Bound}"), Is.EqualTo(header));
    Assert.That((floating.Mode, header.Count), Is.EqualTo(("accurate", 1)));
  }

}
