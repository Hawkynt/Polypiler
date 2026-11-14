using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Catalog.AdapterAudit;
using Polypiler.Tooling.Corpus;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Catalog;

/// <summary>
/// The steps of <c>tests/features/catalog/adp-ncnn-licence.feature</c>. Once per test run a corpus root
/// is prepared in a scratch folder: copies of the repository's <c>tooling/corpus</c>,
/// <c>tooling/licensing</c>, <c>spec/adapters</c> and the negative audits of
/// <c>tests/fixtures/catalog/adapter-audit</c>, and the <c>mediadroid.ncnn.*</c> fixtures imported by
/// the real <c>polypiler-tooling corpus import</c> from a MediaDroid checkout holding the pin — the local
/// one next to the repository, or a shallow fetch. Every scenario runs the real executable there, so the
/// command line of a step is the one the feature shows.
/// </summary>
[Binding, Scope(Feature = "File-level licence audit of the NCNN adapter's native inputs")]
public sealed class AdapterAuditSteps(CommandLog log) {

  private const string Repository = "Hawkynt/MediaDroid";
  private const string Pin = "7f8b6b995cbe54a6afb290a900748393bf01e272";
  private const string Committed = "spec/adapters/ncnn/licence-audit.yaml";
  private const string Fixtures = "tests/fixtures/catalog/adapter-audit";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;
  private static readonly Lazy<string> _corpus = new(PrepareCorpus);

  private string _audit = Committed;

  /// <summary>A checkout of MediaDroid holding the pin: a sibling of the repository or of a folder above it, or a shallow fetch.</summary>
  private static string FindOrFetchCheckout() {
    var git = new GitProcess();
    for (var directory = new DirectoryInfo(_root); directory != null; directory = directory.Parent)
      foreach (var name in (string[])["MediaDroyd.Hawkynt", "MediaDroid", "MediaDroyd"]) {
        var candidate = Path.Combine(directory.FullName, name);
        if (Directory.Exists(candidate) && git.HasCommit(candidate, Pin))
          return candidate;
      }

    var fetched = Scratch.Create("polypiler-mediadroid-checkout-");
    AppDomain.CurrentDomain.ProcessExit += (_, _) => Scratch.Delete(fetched);
    var failure = git.Fetch(fetched, $"https://github.com/{Repository}.git", Pin);
    Assert.That(failure, Is.Null, failure?.Error);
    return fetched;
  }

  /// <summary>Copies what the validator reads into a scratch corpus root and imports the audit's fixtures there.</summary>
  private static string PrepareCorpus() {
    var corpus = Scratch.Create("polypiler-adapter-audit-corpus-");
    AppDomain.CurrentDomain.ProcessExit += (_, _) => Scratch.Delete(corpus);
    foreach (var folder in (string[])["tooling/corpus", "tooling/licensing", "spec/adapters", Fixtures]) {
      var source = Path.Combine(_root, folder);
      foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        Scratch.Write(corpus, Path.Combine(folder, Path.GetRelativePath(source, file)), File.ReadAllBytes(file));
    }

    var fixtures = CorpusManifest.Load(Path.Combine(_root, CorpusManifest.RepositoryPath)).Fixtures
      .Where(fixture => fixture.Id.StartsWith("mediadroid.ncnn.", StringComparison.Ordinal))
      .SelectMany(fixture => (string[])["--fixture", fixture.Id]);
    var import = Toolchain.Tooling(corpus, ["corpus", "import", .. fixtures, "--source", FindOrFetchCheckout()]);
    Assert.That(import.ExitCode, Is.Zero, import.Output);
    return corpus;
  }

  private void Validate(params string[] touches) {
    string[] arguments = ["catalog", "audit-adapter", "--audit", this._audit];
    log.Add(Toolchain.Tooling(_corpus.Value, [.. arguments, .. touches.SelectMany(touch => (string[])["--touch", touch])]));
  }

  private static AdapterAudit Load(string path) => AdapterAudit.Load(Path.Combine(_corpus.Value, path));

  /// <summary>Prepares the corpus root and checks the pin of the repository the audit reads.</summary>
  [Given(@"^the corpus holds (\S+) at ([0-9a-f]{40})$")]
  public void CorpusHolds(string repository, string commit) {
    var manifest = CorpusManifest.Load(Path.Combine(_corpus.Value, CorpusManifest.RepositoryPath));
    Assert.That(manifest.Repository(repository)?.Commit, Is.EqualTo(commit));
    Assert.That((repository, commit), Is.EqualTo((Repository, Pin)));
  }

  /// <summary>Runs a command line of <c>polypiler-tooling</c> in the corpus root.</summary>
  [When("^" + StepText.Quoted + " runs$")]
  public void Runs(string line) {
    var words = CommandLine.Split(line).ToArray();
    Assert.That(words[0], Is.EqualTo("polypiler-tooling"));
    log.Add(Toolchain.Tooling(_corpus.Value, words[1..]));
  }

  /// <summary>Validates the scenario's audit with one touched path.</summary>
  [When(@"^the validator runs with --touch (\S+)$")]
  public void RunsWithTouch(string path) => this.Validate(path);

  /// <summary>Validates the scenario's audit without touched paths.</summary>
  [When("^the validator runs on it$")]
  public void RunsOnIt() => this.Validate();

  /// <summary>Selects a negative audit and checks the digest it records for a path.</summary>
  [Given("^the fixture audit " + StepText.Quoted + " records sha256 " + StepText.Quoted + " for " + StepText.Quoted + "$")]
  public void FixtureRecords(string fixture, string sha256, string path) {
    this.FixtureAudit(fixture);
    Assert.That(Load(this._audit).Record(path)?.Sha256, Is.EqualTo(sha256));
  }

  /// <summary>Selects a negative audit and checks the status of its clearance.</summary>
  [Given("^the fixture audit " + StepText.Quoted + " has clearance status " + StepText.Quoted + "$")]
  public void FixtureClearance(string fixture, string status) {
    this.FixtureAudit(fixture);
    Assert.That(Load(this._audit).Clearance.Status, Is.EqualTo(status));
  }

  /// <summary>Selects a negative audit.</summary>
  [Given("^the fixture audit " + StepText.Quoted + "$")]
  public void FixtureAudit(string fixture) {
    this._audit = $"{Fixtures}/{fixture}";
    Assert.That(File.Exists(Path.Combine(_corpus.Value, this._audit)), Is.True, this._audit);
  }

  /// <summary>Checks the length and digest a record of the audit gives, and that sha256sum's oracle agrees on the imported file.</summary>
  [Then("^the record " + StepText.Quoted + @" has bytes (\d+) and sha256 " + StepText.Quoted + "$")]
  public void RecordHas(string path, long bytes, string sha256) {
    var record = Load(this._audit).Record(path)!;
    Assert.That((record.Bytes, record.Sha256), Is.EqualTo((bytes, sha256)));
    var content = File.ReadAllBytes(CorpusImporter.FilePath(_corpus.Value, Repository, Pin, path));
    Assert.That((content.LongLength, Convert.ToHexStringLower(SHA256.HashData(content))), Is.EqualTo((bytes, sha256)));
  }

  /// <summary>Checks that standard error holds a diagnostic of a code that starts with a message and names a text.</summary>
  [Then(@"^the diagnostic is (PLY\d{4}) " + StepText.Quoted + " naming " + StepText.Quoted + "$")]
  public void DiagnosticNaming(string code, string message, string named) {
    var line = Line(code, message);
    Assert.That(line, Does.Contain(named));
  }

  /// <summary>Checks that standard error holds a diagnostic of a code that starts with a message.</summary>
  [Then(@"^the diagnostic is (PLY\d{4}) " + StepText.Quoted + "$")]
  public void DiagnosticIs(string code, string message) => Assert.That(this.Line(code, message), Is.Not.Null);

  /// <summary>Checks that standard error holds a diagnostic of a code whose line contains a text.</summary>
  [Then(@"^the diagnostic is (PLY\d{4}) containing " + StepText.Quoted + "$")]
  public void DiagnosticContaining(string code, string text)
    => Assert.That(log.Last.Stderr.Split('\n').Where(line => line.Contains($"error {code}: ", StringComparison.Ordinal)), Has.Some.Contains(text), log.Last.Output);

  private string Line(string code, string message) {
    var lines = log.Last.Stderr.Split('\n').Where(line => line.Contains($"error {code}: {message}", StringComparison.Ordinal)).ToList();
    Assert.That(lines, Has.Count.EqualTo(1), log.Last.Output);
    return lines[0];
  }

}
