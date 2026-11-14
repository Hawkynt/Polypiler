using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Corpus;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/infra-corpus.feature</c>. The real <c>polypiler-tooling</c>
/// executable runs in the scenario's workspace, which holds the <c>tooling/corpus</c> files a step
/// writes. The scenarios on <c>Hawkynt/Hawkynt.github.io</c> read a checkout of it: the local one next
/// to the repository when it holds the pin, otherwise one fetched once per test run; a manifest a step
/// writes names that checkout as the repository's URL, so no scenario but the first relies on the
/// network beyond that one fetch.
/// </summary>
[Binding, Scope(Feature = "Corpus importer with locked manifest and fixture discovery")]
public sealed class CorpusSteps(ScenarioWorkspace workspace, CommandLog log) : IDisposable {

  private const string Repository = "Hawkynt/Hawkynt.github.io";
  private const string Pin = "b3a8a8e92cbca83475986391c3cac56f0b185d29";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;
  private static readonly Lazy<string> _checkout = new(FindOrFetchCheckout);

  private MiniUpstream? _mini;
  private string? _source;
  private string _file = "";
  private string _firstLock = "";
  private string _savedLock = "";
  private string _savedManifest = "";

  /// <summary>Deletes the mini-upstream repository of the scenario.</summary>
  public void Dispose() => this._mini?.Dispose();

  /// <summary>The checkout of Hawkynt.github.io holding the pin, shared with the licence scenarios.</summary>
  internal static string Checkout => _checkout.Value;

  /// <summary>The checkout as a <c>file://</c> URL git accepts.</summary>
  internal static string CheckoutUrl => FileUrl(_checkout.Value);

  /// <summary>A checkout of Hawkynt.github.io holding the pin: a sibling of the repository or of a folder above it, or a shallow fetch.</summary>
  private static string FindOrFetchCheckout() {
    var git = new GitProcess();
    for (var directory = new DirectoryInfo(_root); directory != null; directory = directory.Parent) {
      var candidate = Path.Combine(directory.FullName, "Hawkynt.github.io");
      if (Directory.Exists(candidate) && git.HasCommit(candidate, Pin))
        return candidate;
    }

    var fetched = Scratch.Create("polypiler-corpus-checkout-");
    AppDomain.CurrentDomain.ProcessExit += (_, _) => Scratch.Delete(fetched);
    var failure = git.Fetch(fetched, $"https://github.com/{Repository}.git", Pin);
    Assert.That(failure, Is.Null, failure?.Error);
    return fetched;
  }

  /// <summary>A path as a <c>file://</c> URL git accepts, with no character escaped.</summary>
  private static string FileUrl(string path) {
    var slashed = path.Replace('\\', '/');
    return "file://" + (slashed.StartsWith('/') ? "" : "/") + slashed;
  }

  private string Lock => workspace.Resolve(CorpusLock.RepositoryPath);

  private string Manifest => workspace.Resolve(CorpusManifest.RepositoryPath);

  /// <summary>Copies the repository's manifest and lock into the workspace and checks the fixture they name.</summary>
  [Given("^corpus.yaml lists fixture " + StepText.Quoted + " at " + StepText.Quoted + " commit " + StepText.Quoted + "$")]
  public void RepositoryManifestListsFixture(string id, string repository, string commit) {
    workspace.Copy(Path.Combine(_root, CorpusManifest.RepositoryPath), CorpusManifest.RepositoryPath);
    workspace.Copy(Path.Combine(_root, CorpusLock.RepositoryPath), CorpusLock.RepositoryPath);
    var manifest = CorpusManifest.Load(this.Manifest);
    Assert.That(manifest.Fixture(id)?.Repository, Is.EqualTo(repository));
    Assert.That(manifest.Repository(repository)?.Commit, Is.EqualTo(commit));
  }

  /// <summary>Takes the checkout of the pinned repository as the source of the import.</summary>
  [Given("^a local checkout of that repository is passed with --source$")]
  public void CheckoutIsTheSource() => this._source = _checkout.Value;

  /// <summary>Runs a command line of <c>polypiler-tooling</c> with the checkout appended as <c>--source</c>.</summary>
  [When("^I run " + StepText.Quoted + " with that checkout as --source$")]
  public void RunWithSource(string line)
    => log.Add(Toolchain.Tooling(workspace.Path, [.. CommandLine.Split(line), "--source", this._source!]));

  /// <summary>Checks the length of a file of the workspace and keeps it for the next step.</summary>
  [Then("^the file " + StepText.Quoted + @" has (\d+) bytes$")]
  public void FileHasBytes(string path, long bytes) {
    this._file = workspace.Resolve(path);
    Assert.That(new FileInfo(this._file).Length, Is.EqualTo(bytes), log.Last.Output);
  }

  /// <summary>Checks the SHA-256 of the file of the previous step.</summary>
  [Then("^its SHA-256 is " + StepText.Quoted + "$")]
  public void ItsSha256Is(string sha256) => Assert.That(CorpusLockEntry.Hash(File.ReadAllBytes(this._file)), Is.EqualTo(sha256));

  /// <summary>Checks that further files of the same commit were imported next to the file of the previous step.</summary>
  [Then("^(.+) of the same commit are present in the same checkout$")]
  public void PresentInTheSameCheckout(string list) {
    var folder = workspace.Resolve($"{CorpusImporter.UpstreamPath}/{Repository}@{Pin}");
    var named = Regex.Matches(list, StepText.Quoted).Select(match => match.Groups[1].Value).ToArray();
    Assert.That(named, Has.Length.EqualTo(3));
    foreach (var path in named)
      Assert.That(File.Exists(Path.Combine(folder, path)), Is.True, path);
  }

  /// <summary>Builds the mini-upstream repository and writes a manifest with one fixture per file it names.</summary>
  [Given("^the mini-upstream repository with files (.+)$")]
  public void MiniUpstreamWithFiles(string list) {
    var named = Regex.Matches(list, StepText.Quoted).Select(match => match.Groups[1].Value).ToArray();
    Assert.That(named, Is.EquivalentTo(new[] { "a.txt", "dir/b.txt", "LICENSE" }));
    this._mini = MiniUpstream.Create();
    workspace.Write(CorpusManifest.RepositoryPath, this._mini.Manifest(MiniUpstream.Fixture("mini.a", "a.txt") + MiniUpstream.Fixture("mini.b", "dir/b.txt")));
  }

  /// <summary>Runs a command line twice, keeping the lock of the first run.</summary>
  [When("^I run " + StepText.Quoted + " twice$")]
  public void RunTwice(string line) {
    var first = log.Add(Toolchain.Tooling(workspace.Path, [.. CommandLine.Split(line)]));
    Assert.That(first.ExitCode, Is.Zero, first.Output);
    this._firstLock = File.ReadAllText(this.Lock);
    var second = log.Add(Toolchain.Tooling(workspace.Path, [.. CommandLine.Split(line)]));
    Assert.That(second.ExitCode, Is.Zero, second.Output);
  }

  /// <summary>Compares the lock of the second run with that of the first, byte for byte.</summary>
  [Then("^both corpus.lock.json files are byte-identical$")]
  public void LocksAreIdentical() => Assert.That(File.ReadAllText(this.Lock), Is.EqualTo(this._firstLock));

  /// <summary>Checks the paths of the lock in their order.</summary>
  [Then("^the lock lists (.+) in that order$")]
  public void LockLists(string list) {
    var named = Regex.Matches(list, StepText.Quoted).Select(match => match.Groups[1].Value);
    Assert.That(CorpusLock.Load(this.Lock).Entries.Select(entry => entry.Path), Is.EqualTo(named));
  }

  /// <summary>Builds the mini-upstream repository, imports its <c>a.txt</c> and checks what the lock records for it.</summary>
  [Given("^an imported mini-upstream corpus whose lock records " + StepText.Quoted + " with content " + StepText.Quoted + "$")]
  public void ImportedMiniUpstream(string path, string content) {
    this.MiniUpstreamWithFiles("\"a.txt\", \"dir/b.txt\" and \"LICENSE\"");
    var import = Toolchain.Tooling(workspace.Path, "corpus", "import", "--update-lock");
    Assert.That(import.ExitCode, Is.Zero, import.Output);
    var entry = CorpusLock.Load(this.Lock).Find(MiniUpstream.Name, this._mini!.Commit, path)!;
    Assert.That(entry.Sha256, Is.EqualTo(CorpusLockEntry.Hash(System.Text.Encoding.UTF8.GetBytes(StepText.Unescape(content)))));
  }

  /// <summary>Overwrites an imported file of the mini-upstream.</summary>
  [Given("^" + StepText.Quoted + " in the workspace is replaced by " + StepText.Quoted + "$")]
  public void ReplaceImportedFile(string path, string content)
    => Scratch.Write(workspace.Path, $"{CorpusImporter.UpstreamPath}/{MiniUpstream.Name}@{this._mini!.Commit}/{path}", StepText.Unescape(content));

  /// <summary>Writes a manifest of the pinned repository, fetched from the checkout, with one fixture of a path, and keeps the repository's lock.</summary>
  [Given("^corpus.yaml lists a fixture with path " + StepText.Quoted + " at commit " + StepText.Quoted + "$")]
  public void ManifestWithPath(string path, string commit) {
    this.WriteManifest(commit, Fixture("cipher.checksum.missing", Repository, path));
    workspace.Copy(Path.Combine(_root, CorpusLock.RepositoryPath), CorpusLock.RepositoryPath);
    this._savedLock = File.ReadAllText(this.Lock);
  }

  /// <summary>Checks that the lock is the one the scenario started with.</summary>
  [Then("^corpus.lock.json is unchanged$")]
  public void LockUnchanged() => Assert.That(File.ReadAllText(this.Lock), Is.EqualTo(this._savedLock));

  /// <summary>Writes a manifest whose one fixture names a repository it does not declare.</summary>
  [Given("^corpus.yaml lists a fixture " + StepText.Quoted + " with repository " + StepText.Quoted + "$")]
  public void ManifestWithUndeclaredRepository(string id, string repository)
    => this.WriteManifest(Pin, Fixture(id, repository, "a.js"));

  /// <summary>Writes a manifest of the pinned repository, fetched from the checkout, without fixtures.</summary>
  [Given("^the repository " + StepText.Quoted + " at its pin$")]
  public void RepositoryAtItsPin(string repository) {
    Assert.That(repository, Is.EqualTo(Repository));
    this.WriteManifest(Pin, "");
  }

  /// <summary>Counts the stubs on standard output.</summary>
  [Then(@"^stdout lists (\d+) fixture stubs$")]
  public void StdoutListsStubs(int count) => Assert.That(this.StubPaths(), Has.Length.EqualTo(count), log.Last.Output);

  /// <summary>Checks the first and the last path of the stubs.</summary>
  [Then("^the first path is " + StepText.Quoted + " and the last is " + StepText.Quoted + "$")]
  public void FirstAndLast(string first, string last) {
    var paths = this.StubPaths();
    Assert.That((paths[0], paths[^1]), Is.EqualTo((first, last)));
  }

  /// <summary>Checks that the manifest is the one the scenario wrote.</summary>
  [Then("^corpus.yaml is unchanged$")]
  public void ManifestUnchanged() => Assert.That(File.ReadAllText(this.Manifest), Is.EqualTo(this._savedManifest));

  private string[] StubPaths()
    => [.. Regex.Matches(log.Last.Stdout, "^  path: '(.*)'$", RegexOptions.Multiline).Select(match => match.Groups[1].Value)];

  private void WriteManifest(string commit, string fixtures) {
    this._savedManifest = $"""
      schema: 1
      repositories:
        - name: {Repository}
          url: '{FileUrl(_checkout.Value)}'
          commit: {commit}
          licence: LGPL-3.0-or-later
          licenceFiles: [LICENSE, Cipher/LICENSE]
      fixtures:{(fixtures.Length == 0 ? " []" : "\n" + fixtures)}

      """;
    workspace.Write(CorpusManifest.RepositoryPath, this._savedManifest);
  }

  private static string Fixture(string id, string repository, string path) => $"""
      - id: {id}
        repository: {repository}
        path: {path}
        export: main
        dependencies: [Cipher/OpCodes.js]
        run: [node, '{"{file}"}']
        vectors: {"{ kind: embedded, source: inline }"}
        licence: LGPL-3.0-or-later
        determinism: deterministic
        tolerance: exact
        profiles: []

    """;

}
