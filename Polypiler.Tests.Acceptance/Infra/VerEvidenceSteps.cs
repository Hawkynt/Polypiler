using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Evidence;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Toolchains;
using Reqnroll;
using EvidenceIdentity = Polypiler.Core.Verification.Evidence.ScenarioIdentity;
using ScenarioIdentity = Polypiler.Tests.Acceptance.Support.ScenarioIdentity;
using Slug = Polypiler.Core.Verification.Evidence.Slug;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/ver-evidence.feature</c>. The scenarios that end write the
/// scenario's own bundle under <c>artifacts/evidence/</c> with the collector every scenario is given
/// (<see cref="EvidenceHooks"/>), which the hook rewrites after the last step; the negative scenarios
/// build a bundle of a scenario of their own in the workspace and validate it.
/// </summary>
[Binding, Scope(Feature = "Evidence bundle per BDD scenario")]
public sealed class VerEvidenceSteps(EvidenceCollector collector, ScenarioIdentity identity, ScenarioContext scenario, ScenarioWorkspace workspace) {

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private readonly DiagnosticBag _diagnostics = new();
  private EvidenceCollector _other = new();
  private string[] _tags = [];
  private ScenarioOutcome _outcome = ScenarioOutcome.Passed;
  private string _title = "";
  private string _slug = "";
  private string _bundle = "";

  private static string Repository(string path) => Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));

  private static string LockedVersion(string key) => ToolchainLock.Load(Repository(ToolchainLock.RepositoryPath))[key].Version;

  private JsonObject Evidence => JsonNode.Parse(File.ReadAllText(Path.Combine(this._bundle, EvidenceWriter.FileName)))!.AsObject();

  /// <summary>Records a source file of the repository after checking its content.</summary>
  [Given("^the source file " + StepText.Quoted + " with content " + StepText.Quoted + "$")]
  public void SourceFile(string path, string content) {
    Assert.That(File.ReadAllText(Repository(path)), Is.EqualTo(StepText.Unescape(content)));
    collector.AddSource(Repository(path));
  }

  /// <summary>Records an empty generated file of the repository.</summary>
  [Given("^the empty target file " + StepText.Quoted + "$")]
  public void EmptyTargetFile(string path) {
    Assert.That(new FileInfo(Repository(path)).Length, Is.Zero);
    collector.AddTargetCode(Repository(path));
  }

  /// <summary>Records a manifest of the repository.</summary>
  [Given("^the manifest " + StepText.Quoted + "$")]
  public void TheManifest(string path) => collector.AddManifest(Repository(path));

  /// <summary>Records the version <c>tooling/toolchains.lock</c> pins for a tool.</summary>
  [Given("^tool " + StepText.Quoted + " at the version resolved from the toolchain lock$")]
  public void ToolFromTheLock(string key) => collector.AddToolVersion(key, LockedVersion(key));

  /// <summary>Records a process with an empty standard error.</summary>
  [Given("^a process " + StepText.Quoted + " with stdout " + StepText.Quoted + @", empty stderr and exit code (-?\d+)$")]
  public void AProcess(string label, string stdout, int exitCode) => collector.AddProcess(label, StepText.Unescape(stdout), "", exitCode);

  /// <summary>Records a byte-for-byte comparison of vectors that all have the same reference and target output.</summary>
  [Given(@"^a bit-exact comparison of (\d+) vectors? with reference " + StepText.Quoted + " and target " + StepText.Quoted + "$")]
  public void BitExactComparison(int count, string reference, string target) {
    var policy = new ExactnessPolicy.Exact();
    var vectors = Enumerable.Range(1, count).Select(index => new TestVector($"vector-{index}", [], null)).ToList();
    collector.AddComparison(DifferentialComparer.Compare(vectors,
      [.. vectors.Select(_ => Convert.FromHexString(reference))], [.. vectors.Select(_ => Convert.FromHexString(target))], policy), policy);
  }

  /// <summary>Writes the scenario's bundle as the hook does after the last step, as a passed scenario.</summary>
  [When("^the scenario ends$")]
  public void ScenarioEnds() => this._bundle = EvidenceWriter.Write(collector, EvidenceHooks.Identity(identity, scenario), ScenarioOutcome.Passed, EvidenceHooks.Root);

  /// <summary>Checks where the bundle was written.</summary>
  [Then("^the bundle directory is " + StepText.Quoted + "$")]
  public void BundleDirectoryIs(string path) => Assert.That(Path.GetRelativePath(_root, this._bundle).Replace('\\', '/'), Is.EqualTo(path));

  /// <summary>Checks the hash of the first source.</summary>
  [Then("^\"evidence.json\" records source hash " + StepText.Quoted + "$")]
  public void SourceHash(string hash) => Assert.That(this.Evidence["sources"]![0]!["sha256"]!.GetValue<string>(), Is.EqualTo(hash));

  /// <summary>Checks the hash of the first generated file.</summary>
  [Then("^it records target code hash " + StepText.Quoted + "$")]
  public void TargetCodeHash(string hash) => Assert.That(this.Evidence["targetCode"]![0]!["sha256"]!.GetValue<string>(), Is.EqualTo(hash));

  /// <summary>
  /// Checks the policy, the diff and the CI link. <c>bit-exact</c> is the exact mode on bytes: no
  /// element format. The link is <c>local</c> unless the GitHub Actions variables are set.
  /// </summary>
  [Then("^it records exactness \"bit-exact\", an empty " + StepText.Quoted + " and the ciLink \"local\" outside CI$")]
  public void BitExactEmptyDiffAndLink(string diff) {
    var evidence = this.Evidence;
    Assert.That(evidence["exactness"]!["mode"]!.GetValue<string>(), Is.EqualTo("exact"));
    Assert.That(evidence["exactness"]!.AsObject().ContainsKey("format"), Is.False);
    Assert.That(evidence["diff"]!.GetValue<string>(), Is.EqualTo(diff));
    Assert.That(new FileInfo(Path.Combine(this._bundle, diff)).Length, Is.Zero);
    var link = EvidenceWriter.CiLink(Environment.GetEnvironmentVariable);
    Assert.That(evidence["ciLink"]!.GetValue<string>(), Is.EqualTo(link));
    if (Environment.GetEnvironmentVariable("GITHUB_RUN_ID") is null)
      Assert.That(link, Is.EqualTo("local"));
  }

  /// <summary>Validates the bundle.</summary>
  [Then("^the bundle validates without diagnostics$")]
  public void ValidatesWithoutDiagnostics() {
    Assert.That(EvidenceValidator.Validate(this._bundle, this._diagnostics), Is.True, string.Join(Environment.NewLine, this._diagnostics.Items));
    Assert.That(this._diagnostics.Items, Is.Empty);
  }

  /// <summary>Takes a title.</summary>
  [Given("^the title " + StepText.Quoted + "$")]
  public void Title(string title) => this._title = title;

  /// <summary>Computes the slug of the title.</summary>
  [When("^its slug is computed$")]
  public void SlugIsComputed() => this._slug = Slug.From(this._title);

  /// <summary>Checks the slug.</summary>
  [Then("^the slug is " + StepText.Quoted + "$")]
  public void SlugIs(string slug) => Assert.That(this._slug, Is.EqualTo(slug));

  /// <summary>Starts the bundle of another scenario with every item a translation needs but the comparison.</summary>
  [Given("^a bundle for a scenario tagged " + StepText.Quoted + " with source, target code, manifest, tool version and process$")]
  public void BundleWithoutComparison(string tag) => this.OtherScenario(tag);

  /// <summary>Checks that the other scenario has no comparison.</summary>
  [Given("^no comparison$")]
  public void NoComparison() => Assert.That(this._other.Comparison, Is.Null);

  /// <summary>Starts the bundle of another scenario with an outcome and every item a translation needs but the comparison.</summary>
  [Given("^a bundle with outcome " + StepText.Quoted + " for a scenario tagged " + StepText.Quoted + "$")]
  public void BundleWithOutcome(string outcome, string tag) {
    this.OtherScenario(tag);
    this._outcome = Enum.Parse<ScenarioOutcome>(outcome, true);
  }

  /// <summary>Adds a byte-for-byte comparison of one vector to the other scenario.</summary>
  [Given("^a comparison with reference " + StepText.Quoted + " and target " + StepText.Quoted + "$")]
  public void AComparison(string reference, string target) {
    var policy = new ExactnessPolicy.Exact();
    this._other.AddComparison(DifferentialComparer.Compare([new TestVector("vector-1", [], null)], [Convert.FromHexString(reference)], [Convert.FromHexString(target)], policy), policy);
  }

  /// <summary>Writes the other scenario's bundle into the workspace and validates it.</summary>
  [When("^the bundle is validated$")]
  public void BundleIsValidated() {
    this._bundle = EvidenceWriter.Write(this._other, new EvidenceIdentity("Other feature", "Other scenario", this._tags), this._outcome, workspace.Resolve("evidence"));
    EvidenceValidator.Validate(this._bundle, this._diagnostics);
  }

  /// <summary>Checks a diagnostic's code and part of its message.</summary>
  [Then("^diagnostic (PLY\\d{4}) is reported with " + StepText.Quoted + "$")]
  public void ReportedWith(string code, string text)
    => Assert.That(this._diagnostics.Items.Where(diagnostic => diagnostic.Code == code && diagnostic.Message.Contains(text, StringComparison.Ordinal)), Is.Not.Empty,
      string.Join(Environment.NewLine, this._diagnostics.Items));

  /// <summary>Checks the exit code the diagnostics map to.</summary>
  [Then(@"^the mapped exit code is (\d+)$")]
  public void MappedExitCode(int exitCode) => Assert.That((int)this._diagnostics.ExitCode, Is.EqualTo(exitCode));

  /// <summary>Checks a file of the bundle.</summary>
  [Then("^" + StepText.Quoted + " contains " + StepText.Quoted + "$")]
  public void BundleFileContains(string file, string text) => Assert.That(File.ReadAllText(Path.Combine(this._bundle, file)), Does.Contain(text));

  /// <summary>Checks that this scenario lacks a tag and records a tool at its locked version.</summary>
  [Given("^a scenario without the " + StepText.Quoted + " tag that recorded tool " + StepText.Quoted + "$")]
  public void ScenarioWithoutTag(string tag, string key) {
    Assert.That(scenario.ScenarioInfo.CombinedTags, Does.Not.Contain(tag.TrimStart('@')));
    collector.AddToolVersion(key, LockedVersion(key));
  }

  /// <summary>Checks that two properties of <c>evidence.json</c> are <c>null</c>.</summary>
  [Then("^\"evidence.json\" has " + StepText.Quoted + " null and " + StepText.Quoted + " null$")]
  public void PropertiesAreNull(string first, string second) {
    var evidence = this.Evidence;
    Assert.That(evidence.ContainsKey(first) && evidence.ContainsKey(second), Is.True);
    Assert.That((evidence[first], evidence[second]), Is.EqualTo(((JsonNode?)null, (JsonNode?)null)));
  }

  private void OtherScenario(string tag) {
    this._tags = [tag];
    this._other = new EvidenceCollector();
    this._other.AddSource(Repository("tests/fixtures/verify/evidence/source.txt"));
    this._other.AddTargetCode(Repository("tests/fixtures/verify/evidence/target.txt"));
    this._other.AddManifest(Repository("tests/fixtures/verify/evidence/sample.manifest.json"));
    this._other.AddToolVersion("node", LockedVersion("node"));
    this._other.AddProcess("run", "b90f2ae4", "", 0);
  }

}
