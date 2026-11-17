using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Polypiler.Core.Verification.Capabilities;
using Polypiler.Core.Verification.Gates;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tests.Verification.Capabilities;

/// <summary>
/// <c>polypiler-tooling report capabilities</c> in process, in a workspace of its own: writing,
/// <c>--check</c> against committed files that agree, claim more, claim less or were edited, and every
/// refusal with its code and exit code.
/// </summary>
[TestFixture]
public sealed class ReportCapabilitiesCommandTests {

  private const string Json = "docs/capabilities.json";

  private const string Markdown = "docs/capabilities.md";

  private CapabilityWorkspace _workspace = null!;

  [SetUp]
  public void CreateWorkspace() => this._workspace = new("one-verified");

  [TearDown]
  public void DeleteWorkspace() => this._workspace.Dispose();

  private string Read(string relative) => File.ReadAllText(this._workspace.PathOf(relative));

  private void Edit(string relative, string original, string replacement) {
    var text = this.Read(relative);
    Assert.That(text, Does.Contain(original));
    File.WriteAllText(this._workspace.PathOf(relative), text.Replace(original, replacement, StringComparison.Ordinal));
  }

  private CapabilityReport Committed() => CapabilityReport.Load(File.OpenRead(this._workspace.PathOf(Json)));

  /// <summary>Scenario "Without gate records every pair is planned", through the command.</summary>
  [Test]
  public void WithoutGateRecords_TheCommandWritesFourPlannedPairs() {
    Assert.That(this._workspace.Run(), Is.EqualTo((0, $"wrote {Json} and {Markdown}: 4 pairs from 0 gate records{Environment.NewLine}", "")));
    var report = this.Committed();
    Assert.That(report.Pairs, Has.Count.EqualTo(4));
    Assert.That(report.Pairs.Select(pair => (pair.State, pair.Fixtures, pair.Evidence)), Is.All.EqualTo((GateState.Planned, 0, (string?)null)));
    Assert.That(Directory.GetFiles(this._workspace.PathOf("docs")).Select(Path.GetFileName), Is.EquivalentTo(new[] { "capabilities.json", "capabilities.md" }));
  }

  /// <summary>Scenario "A verified pair is reported at its achieved state and the others stay planned".</summary>
  [Test]
  public void AVerifiedPair_IsWrittenAtItsState() {
    this._workspace.Record(CapabilityWorkspace.CSharpToJavaScript, "add32", GateState.Verified);
    Assert.That(this._workspace.Run().ExitCode, Is.Zero);
    var report = this.Committed();
    Assert.That((report.StateOf("csharp", "net10", "javascript", "node-es2022"), report.Find("csharp", "net10", "javascript", "node-es2022")!.Fixtures), Is.EqualTo((GateState.Verified, 1)));
    Assert.That(report.StateOf("javascript", "node-es2022", "csharp", "net10"), Is.EqualTo(GateState.Planned));
    var markdown = this.Read(Markdown);
    Assert.That(markdown.IndexOf("| net10 | planned | verified |", StringComparison.Ordinal), Is.GreaterThan(markdown.IndexOf("## csharp", StringComparison.Ordinal)).And.LessThan(markdown.IndexOf("## javascript", StringComparison.Ordinal)));
  }

  /// <summary>Scenario "The weakest fixture decides the pair".</summary>
  [Test]
  public void TheWeakestFixture_DecidesThePair() {
    this._workspace.Record(CapabilityWorkspace.CSharpToJavaScript, "add32", GateState.Verified);
    this._workspace.Record(CapabilityWorkspace.CSharpToJavaScript, "mul64", GateState.Emitted);
    Assert.That(this._workspace.Run().ExitCode, Is.Zero);
    var pair = this.Committed().Find("csharp", "net10", "javascript", "node-es2022")!;
    Assert.That((pair.State, pair.Fixtures), Is.EqualTo((GateState.Emitted, 2)));
  }

  /// <summary>Scenario "A hand-raised cell fails the check", and test design 6 (committed higher).</summary>
  [Test]
  public void AHandRaisedCell_FailsTheCheck() {
    this._workspace.Run();
    const string field = "\n      ";
    this.Edit(Json,
      $"\"to\": \"csharp\",{field}\"targetProfile\": \"net10\",{field}\"state\": \"planned\",{field}\"fixtures\": 0,{field}\"evidence\": null,{field}\"limitations\": []",
      $"\"to\": \"csharp\",{field}\"targetProfile\": \"net10\",{field}\"state\": \"verified\",{field}\"fixtures\": 1,{field}\"evidence\": \"artifacts/gates/javascript--csharp--node-es2022--net10.json\",{field}\"limitations\": []");
    Assert.That(this.Read(Json), Does.Contain("\"from\": \"javascript\",\n      \"sourceProfile\": \"node-es2022\",\n      \"to\": \"csharp\",\n      \"targetProfile\": \"net10\",\n      \"state\": \"verified\""));
    var before = this.Read(Json);
    var (exitCode, output, error) = this._workspace.Run("--check");
    Assert.That((exitCode, output), Is.EqualTo((1, "")));
    Assert.That(CapabilityWorkspace.Lines(error), Is.EqualTo(new[] {
      $"polypiler-tooling: error PLY0061: {Json}: pair javascript/node-es2022 -> csharp/net10 is committed as 'verified' but the evidence gives 'planned'",
      $"polypiler-tooling: error PLY0091: {Markdown} does not match {Json}"
    }));
    Assert.That(this.Read(Json), Is.EqualTo(before));
  }

  /// <summary>Test design 2 and 6 (equal): an up-to-date report passes and nothing is written.</summary>
  [Test]
  public void AnUpToDateReport_PassesTheCheck_AndNothingIsWritten() {
    this._workspace.Record(CapabilityWorkspace.CSharpToJavaScript, "add32", GateState.Verified);
    this._workspace.Run();
    var written = File.GetLastWriteTimeUtc(this._workspace.PathOf(Json));
    Assert.That(this._workspace.Run("--check"), Is.EqualTo((0, $"{Json} and {Markdown} up to date: 4 pairs{Environment.NewLine}", "")));
    Assert.That(File.GetLastWriteTimeUtc(this._workspace.PathOf(Json)), Is.EqualTo(written));
    Assert.That(Directory.GetFiles(this._workspace.PathOf("docs")), Has.Length.EqualTo(2));
  }

  /// <summary>Test design 6 (committed lower): a pair that reached more than the committed file says is stale.</summary>
  [Test]
  public void ACellBelowTheEvidence_IsStale() {
    this._workspace.Run();
    this._workspace.Record(CapabilityWorkspace.CSharpToJavaScript, "add32", GateState.Compiled);
    var (exitCode, _, error) = this._workspace.Run("--check");
    Assert.That((exitCode, CapabilityWorkspace.Lines(error)), Is.EqualTo((1, new[] {
      $"polypiler-tooling: error PLY0091: {Json} is stale: pair csharp/net10 -> javascript/node-es2022 reached 'compiled', committed 'planned'"
    })));
  }

  /// <summary>A pair at the same state whose fixtures, evidence or limitations changed is stale too.</summary>
  [Test]
  public void ACellWithOtherDetails_IsStale() {
    this._workspace.Run();
    this.Edit("spec/languages/csharp/profiles/net10.yaml", "decimal arithmetic", "decimal");
    var (exitCode, _, error) = this._workspace.Run("--check");
    Assert.That((exitCode, CapabilityWorkspace.Lines(error)), Is.EqualTo((1, new[] {
      $"polypiler-tooling: error PLY0091: {Json} is stale: pair csharp/net10 -> csharp/net10 differs from the evidence in its fixtures, evidence or limitations",
      $"polypiler-tooling: error PLY0091: {Json} is stale: pair csharp/net10 -> javascript/node-es2022 differs from the evidence in its fixtures, evidence or limitations"
    })));
  }

  /// <summary>Scenario "A gate record with a wrong evidence hash contributes nothing".</summary>
  [Test]
  public void ARecordWithAWrongHash_ContributesNothing() {
    this._workspace.Record(CapabilityWorkspace.CSharpToJavaScript, "add32", GateState.Verified);
    File.AppendAllText(this._workspace.PathOf("artifacts/evidence/manifests/verified.json"), "\n");
    var (exitCode, _, error) = this._workspace.Run();
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(error, Does.Contain("PLY0058").And.Contain("evidence hash differs"));
    Assert.That(this.Committed().StateOf("csharp", "net10", "javascript", "node-es2022"), Is.EqualTo(GateState.Planned));
  }

  /// <summary>Test design 13 through the command.</summary>
  [Test]
  public void ARecordWithoutSpecDocuments_FailsWithPly0061() {
    this._workspace.Record(new("cobol", "csharp", "x", "net10"), "add32", GateState.Parsed);
    var (exitCode, _, error) = this._workspace.Run();
    Assert.That((exitCode, CapabilityWorkspace.Lines(error)), Is.EqualTo((1, new[] {
      "polypiler-tooling: error PLY0061: gate record 'artifacts/gates/cobol--csharp--x--net10.json' names a pair with no language or target document"
    })));
  }

  /// <summary>Scenario "The same inputs give the same bytes", through the command into two output directories.</summary>
  [Test]
  public void TwoRuns_WriteTheSameBytes() {
    using var mixed = new CapabilityWorkspace("mixed");
    mixed.Record(CapabilityWorkspace.CSharpToJavaScript, "add32", GateState.Verified);
    mixed.Record(new("csharp", "csharp", "net8", "net10"), "add32", GateState.Executed);
    Assert.That(mixed.Run("--out", "first").ExitCode, Is.Zero);
    Assert.That(mixed.Run("--out", "second").ExitCode, Is.Zero);
    foreach (var name in new[] { "capabilities.json", "capabilities.md" })
      Assert.That(SHA256.HashData(File.ReadAllBytes(mixed.PathOf("first/" + name))), Is.EqualTo(SHA256.HashData(File.ReadAllBytes(mixed.PathOf("second/" + name)))));
  }

  /// <summary>Test design 8 through the command.</summary>
  [Test]
  public void AnEmptySpecTree_WritesNoPair() {
    using var empty = new CapabilityWorkspace("empty");
    Assert.That(empty.Run().ExitCode, Is.Zero);
    Assert.That(File.ReadAllText(empty.PathOf(Json)), Does.Contain("\"pairs\": []"));
    Assert.That(empty.Run("--check").ExitCode, Is.Zero);
  }

  /// <summary>Test design 15: a gate directory that does not exist is an environment error, never an empty set of records.</summary>
  [TestCase("--gates", "nowhere", "'nowhere' cannot be read: the gate directory does not exist")]
  [TestCase("--spec", "nospec", "'nospec' cannot be read: the spec root does not exist")]
  public void AMissingDirectory_IsAnEnvironmentError(string option, string value, string message) {
    foreach (var check in new[] { false, true }) {
      var (exitCode, _, error) = this._workspace.Run(check ? [option, value, "--check"] : [option, value]);
      Assert.That((exitCode, error), Is.EqualTo((1, $"polypiler-tooling: error PLY0004: {message}{Environment.NewLine}")));
    }

    Assert.That(Directory.Exists(this._workspace.PathOf("docs")), Is.False);
  }

  /// <summary>Test design 16: an output directory that cannot be created, and a file that cannot be replaced, leave no partial file.</summary>
  [Test]
  public void AnOutputThatCannotBeWritten_LeavesNoPartialFile() {
    File.WriteAllText(this._workspace.PathOf("blocked"), "a file, not a directory");
    var (exitCode, _, error) = this._workspace.Run("--out", "blocked");
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(error, Does.StartWith("polypiler-tooling: error PLY0005: the output directory 'blocked' cannot be written: "));

    Directory.CreateDirectory(this._workspace.PathOf("docs/capabilities.md"));
    (exitCode, _, error) = this._workspace.Run();
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(error, Does.StartWith("polypiler-tooling: error PLY0005: the output directory 'docs' cannot be written: "));
    Assert.That(Directory.GetFileSystemEntries(this._workspace.PathOf("docs")).Select(Path.GetFileName), Is.EquivalentTo(new[] { "capabilities.json", "capabilities.md" }));
    Assert.That(Directory.Exists(this._workspace.PathOf("docs/capabilities.md")), Is.True);
  }

  /// <summary>Test design 17: a committed report that breaks its schema is a PLY0040 naming the pointer.</summary>
  [Test]
  public void ACommittedReportThatBreaksTheSchema_IsPly0040() {
    this._workspace.Run();
    this.Edit(Json, "\"state\": \"planned\"", "\"state\": \"supported\"");
    var (exitCode, _, error) = this._workspace.Run("--check");
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(CapabilityWorkspace.Lines(error), Has.Some.StartWith($"polypiler-tooling: error PLY0040: {Json}: /pairs/0/state: "));
  }

  /// <summary>Test design 18: a hand-edited Markdown cell.</summary>
  [Test]
  public void AHandEditedMarkdownCell_IsPly0091() {
    this._workspace.Run();
    this.Edit(Markdown, "| net10 | planned | planned |", "| net10 | planned | verified |");
    Assert.That(this._workspace.Run("--check"), Is.EqualTo((1, "", $"polypiler-tooling: error PLY0091: {Markdown} does not match {Json}{Environment.NewLine}")));
  }

  /// <summary>A Markdown file with CRLF line ends, as a checkout may convert it, is up to date.</summary>
  [Test]
  public void AMarkdownFileWithCrLf_IsUpToDate() {
    this._workspace.Run();
    File.WriteAllText(this._workspace.PathOf(Markdown), this.Read(Markdown).Replace("\n", "\r\n", StringComparison.Ordinal));
    Assert.That(this._workspace.Run("--check").ExitCode, Is.Zero);
  }

  /// <summary>Test design 19: one new target profile with two source profiles adds two pairs.</summary>
  [Test]
  public void AChangedPairSet_IsPly0091() {
    this._workspace.Run();
    var target = this._workspace.PathOf("spec/targets/csharp/net8/target.yaml");
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.WriteAllText(target, this.Read("spec/targets/csharp/net10/target.yaml").Replace("net10", "net8", StringComparison.Ordinal));
    var (exitCode, _, error) = this._workspace.Run("--check");
    Assert.That((exitCode, CapabilityWorkspace.Lines(error)), Is.EqualTo((1, new[] { $"polypiler-tooling: error PLY0091: {Json}: pair set differs from spec/ (2 added, 0 removed)" })));

    File.Delete(target);
    File.Delete(this._workspace.PathOf("spec/languages/javascript/profiles/node-es2022.yaml"));
    (exitCode, _, error) = this._workspace.Run("--check");
    Assert.That((exitCode, CapabilityWorkspace.Lines(error)), Is.EqualTo((1, new[] { $"polypiler-tooling: error PLY0091: {Json}: pair set differs from spec/ (0 added, 2 removed)" })));
  }

  /// <summary>A missing committed file under --check.</summary>
  [Test]
  public void AMissingCommittedFile_IsPly0091() {
    Assert.That(this._workspace.Run("--check"), Is.EqualTo((1, "", $"polypiler-tooling: error PLY0091: generated document out of date: '{Json}' is missing{Environment.NewLine}")));
    this._workspace.Run();
    File.Delete(this._workspace.PathOf(Markdown));
    Assert.That(this._workspace.Run("--check"), Is.EqualTo((1, "", $"polypiler-tooling: error PLY0091: generated document out of date: '{Markdown}' is missing{Environment.NewLine}")));
  }

  /// <summary>A spec file that is no YAML stops the command with its PLY0047 before anything is written.</summary>
  [Test]
  public void AnUnreadableSpecFile_StopsTheCommand() {
    File.WriteAllText(this._workspace.PathOf("spec/targets/csharp/net10/target.yaml"), "id: [unclosed");
    var (exitCode, _, error) = this._workspace.Run();
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(error, Does.Contain("error PLY0047: "));
    Assert.That(Directory.Exists(this._workspace.PathOf("docs")), Is.False);
  }

  /// <summary>The commit: --commit, else the environment's when it is a hash, else forty zeros.</summary>
  [TestCase(null, "0000000000000000000000000000000000000000")]
  [TestCase("not a hash", "0000000000000000000000000000000000000000")]
  [TestCase("89abcdef0123456789abcdef0123456789abcdef", "89abcdef0123456789abcdef0123456789abcdef")]
  public void TheCommit_ComesFromTheOptionOrTheEnvironment(string? environment, string expected) {
    Assert.That(ReportCapabilitiesCommand.Run([], TextWriter.Null, TextWriter.Null, this._workspace.Root, environment), Is.Zero);
    Assert.That(this.Committed().Commit, Is.EqualTo(expected));
    Assert.That(ReportCapabilitiesCommand.Run(["--commit", "fedcba9876543210fedcba9876543210fedcba98"], TextWriter.Null, TextWriter.Null, this._workspace.Root, environment), Is.Zero);
    Assert.That(this.Committed().Commit, Is.EqualTo("fedcba9876543210fedcba9876543210fedcba98"));
  }

  [TestCase(new[] { "--check", "--check" }, "option '--check' is given more than once")]
  [TestCase(new[] { "--frobnicate" }, "unknown option '--frobnicate'")]
  [TestCase(new[] { "--gates" }, "option '--gates' requires a value")]
  [TestCase(new[] { "--out", "a", "--out", "b" }, "option '--out' is given more than once")]
  [TestCase(new[] { "--commit", "HEAD" }, "'--commit' needs 40 lower-case hexadecimal digits, not 'HEAD'")]
  public void ACommandLineThatCannotBeUnderstood_IsAUsageError(string[] args, string message)
    => Assert.That(this._workspace.Run(args), Is.EqualTo((2, "", $"polypiler-tooling: error PLY0003: {message}{Environment.NewLine}")));

  /// <summary>The verb is reached through the program, and its usage line is listed.</summary>
  [Test]
  public void TheProgram_DispatchesTheReport() {
    var (output, error) = (new StringWriter(), new StringWriter());
    Assert.That(Program.Run([], output, error), Is.Zero);
    Assert.That(output.ToString(), Does.Contain("report capabilities [--gates <dir>] [--spec <dir>] [--out <dir>] [--commit <sha>] [--check]"));
    Assert.That(Program.Run(["report", "capabilities", "--frobnicate"], output, error), Is.EqualTo(2));
    Assert.That(error.ToString(), Does.Contain("unknown option '--frobnicate'"));
    Assert.That(() => ReportCapabilitiesCommand.Run(["--frobnicate"], TextWriter.Null, TextWriter.Null), Throws.Nothing);
  }

  /// <summary>Profiles: documents that are no profile are skipped.</summary>
  [Test]
  public void Profiles_SkipsDocumentsThatAreNoProfile() {
    var spec = this._workspace.PathOf("spec");
    File.WriteAllText(Path.Combine(spec, "languages", "csharp", "profiles", "odd.yaml"), "id: LANGUAGES.csharp.odd\nspec:\n  kind: profile\n  language: LANGUAGES.javascript\n");
    File.WriteAllText(Path.Combine(spec, "languages", "csharp", "profiles", "nolang.yaml"), "id: LANGUAGES.csharp.nolang\nspec:\n  kind: profile\n  language: csharp\n");
    File.WriteAllText(Path.Combine(spec, "languages", "csharp", "profiles", "plain.yaml"), "id: LANGUAGES.csharp.plain\nspec:\n  kind: profile\n  language: LANGUAGES.csharp\n");
    File.WriteAllText(Path.Combine(spec, "languages", "csharp", "profiles", "nobody.yaml"), "id: LANGUAGES.csharp.nobody\n");
    File.WriteAllText(Path.Combine(spec, "languages", "csharp", "profiles", "noid.yaml"), "spec:\n  kind: profile\n  language: LANGUAGES.csharp\n");
    File.WriteAllText(Path.Combine(spec, "languages", "csharp", "profiles", "constructs.yaml"),
      "id: LANGUAGES.csharp.constructs\nspec:\n  kind: profile\n  language: LANGUAGES.csharp\n  constructs:\n    - text\n    - {construct: a, state: diagnosed}\n    - {state: diagnosed}\n    - {construct: b, state: supported}\n");
    Directory.CreateDirectory(Path.Combine(spec, "targets", "csharp", "odd"));
    File.WriteAllText(Path.Combine(spec, "targets", "csharp", "odd", "target.yaml"), "id: TARGETS.csharp.odd\nspec:\n  language: LANGUAGES.csharp\n  profile: other\n");
    File.WriteAllText(Path.Combine(spec, "targets", "csharp", "odd", "noprofile.yaml"), "id: TARGETS.csharp.noprofile\nspec:\n  language: LANGUAGES.csharp\n");
    var (sources, targets) = ReportCapabilitiesCommand.Profiles(SpecTree.Load(spec, this._workspace.Root));
    Assert.That(sources.Select(source => $"{source.Language}/{source.Profile}:{source.Limitations.Count}"), Is.EqualTo(new[] { "csharp/constructs:1", "csharp/net10:1", "csharp/plain:0", "javascript/node-es2022:0" }));
    Assert.That(sources[0].Limitations.Single(), Is.EqualTo(new CapabilityLimitation("a", "")));
    Assert.That(targets.Select(target => $"{target.Language}/{target.Profile}"), Is.EqualTo(new[] { "csharp/net10", "javascript/node-es2022" }));
  }

  /// <summary>The repository's own committed report: generated from spec/ without gate records, every pair planned, and up to date.</summary>
  [Test, Category("Slow")]
  public void TheCommittedReport_IsUpToDate_AndEveryPairIsPlanned() {
    var root = PrototypeFixture.RepositoryRoot;
    var gates = Path.Combine(this._workspace.Root, "no-gates");
    Directory.CreateDirectory(gates);
    var (output, error) = (new StringWriter(), new StringWriter());
    Assert.That(ReportCapabilitiesCommand.Run(["--gates", gates, "--check"], output, error, root, null), Is.Zero, error.ToString());
    var committed = CapabilityReport.Load(File.OpenRead(Path.Combine(root, "docs", "capabilities.json")));
    Assert.That(committed.Pairs, Is.Not.Empty.And.All.Property(nameof(CapabilityPair.State)).EqualTo(GateState.Planned));
    Assert.That(committed.GateRecords, Is.Zero);
  }

}
