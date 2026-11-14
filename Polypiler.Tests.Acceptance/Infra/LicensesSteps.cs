using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Licensing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/infra-licenses.feature</c>. Every scenario's workspace holds a
/// copy of the repository's <c>tooling/licensing</c>, so the real <c>polypiler-tooling</c> executable
/// finds the policy there; a tree named by a step is copied from <c>tests/fixtures/infra/licenses</c>
/// under its own name. The corpus scenario imports the Cipher fixture from the checkout the corpus
/// scenarios use, with a manifest and lock that hold only its repository.
/// </summary>
[Binding, Scope(Feature = "Licence scanner and policy gate")]
public sealed class LicensesSteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Repository = "Hawkynt/Hawkynt.github.io";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;
  private static readonly string _fixtures = Path.Combine(_root, "tests", "fixtures", "infra", "licenses");

  private static LicenceTexts Texts { get; } = LicenceTexts.Load(Path.Combine(_root, "tooling", "licensing", LicencePolicy.TextsFolder));

  /// <summary>Copies a folder of the repository into the workspace under a relative path.</summary>
  private void CopyFolder(string source, string path) {
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, Path.Combine(path, Path.GetRelativePath(source, file)));
  }

  /// <summary>Copies the licensing folder and a synthetic tree into the workspace and returns the tree's folder there.</summary>
  private string Tree(string tree) {
    this.CopyFolder(Path.Combine(_root, "tooling", "licensing"), Path.Combine("tooling", "licensing"));
    this.CopyFolder(Path.Combine(_fixtures, tree), tree);
    return workspace.Resolve(tree);
  }

  private static string? Identify(string file) => Texts.Identify(File.ReadAllText(file)) is { } licences ? LicenceTexts.Family(licences) : null;

  /// <summary>Writes a manifest and lock of the pinned Cipher repository alone, imports them from the checkout, and checks the fixture's declared licence.</summary>
  [Given("^the imported corpus fixture " + StepText.Quoted + ", declared " + StepText.Quoted + " in corpus.yaml$")]
  public void ImportedCorpusFixture(string id, string licence) {
    this.CopyFolder(Path.Combine(_root, "tooling", "licensing"), Path.Combine("tooling", "licensing"));
    var manifest = CorpusManifest.Load(Path.Combine(_root, CorpusManifest.RepositoryPath));
    var fixture = manifest.Fixture(id)!;
    var repository = manifest.Repository(fixture.Repository)!;
    Assert.That((fixture.Licence, repository.Name), Is.EqualTo((licence, Repository)));

    var checkout = CorpusSteps.Checkout;
    workspace.Write(CorpusManifest.RepositoryPath, $"""
      schema: 1
      repositories:
        - name: {repository.Name}
          url: '{CorpusSteps.CheckoutUrl}'
          commit: {repository.Commit}
          licence: {repository.Licence}
          licenceFiles: [{string.Join(", ", repository.LicenceFiles)}]
      fixtures:
        - id: {fixture.Id}
          repository: {fixture.Repository}
          path: {fixture.Path}
          export: {fixture.Export}
          dependencies: [{string.Join(", ", fixture.Dependencies)}]
          run: [node, '{"{file}"}']
          vectors: {"{ kind: embedded, source: tests }"}
          licence: {fixture.Licence}
          determinism: {fixture.Determinism}
          tolerance: {fixture.Tolerance}
          profiles: []

      """);
    var locked = CorpusLock.Load(Path.Combine(_root, CorpusLock.RepositoryPath)).Entries.Where(entry => entry.Repository == Repository);
    workspace.Write(CorpusLock.RepositoryPath, new CorpusLock(locked).ToJson());
    var import = Toolchain.Tooling(workspace.Path, "corpus", "import", "--source", checkout);
    Assert.That(import.ExitCode, Is.Zero, import.Output);
  }

  /// <summary>Checks the declared licence and licence files of a repository of the workspace's manifest.</summary>
  [Given("^corpus.yaml declares " + StepText.Quoted + " as " + StepText.Quoted + " with licence files " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void ManifestDeclares(string name, string licence, string first, string second) {
    var repository = CorpusManifest.Load(workspace.Resolve(CorpusManifest.RepositoryPath)).Repository(name)!;
    Assert.That((repository.Licence, repository.LicenceFiles), Is.EqualTo((licence, new[] { first, second })));
  }

  /// <summary>Copies the nested tree and checks its two licence files and its declaration for the sub-folder.</summary>
  [Given("^the tree " + StepText.Quoted + " with an (\\S+) " + StepText.Quoted + " at the root and a (\\S+) " + StepText.Quoted + " in " + StepText.Quoted + ", declared " + StepText.Quoted + " for " + StepText.Quoted + "$")]
  public void NestedTree(string tree, string rootFamily, string rootFile, string subFamily, string subFile, string sub, string declared, string glob) {
    var folder = this.Tree(tree);
    Assert.That(Identify(Path.Combine(folder, rootFile)), Is.EqualTo(rootFamily));
    Assert.That(Identify(Path.Combine(folder, sub, subFile)), Is.EqualTo(subFamily));
    Assert.That(LicenceDeclarations.Load(folder).For(glob.Replace("**", "x.c")), Is.EqualTo(declared));
  }

  /// <summary>Copies a tree and checks the first line of one of its files.</summary>
  [Given("^the tree " + StepText.Quoted + " whose file " + StepText.Quoted + " starts with " + StepText.Quoted + "$")]
  public void TreeWithHeader(string tree, string file, string line) => this.HeaderTree(tree, file, line);

  /// <summary>Copies a tree and checks the first line of one of its files and the licence file beside it.</summary>
  [Given("^the tree " + StepText.Quoted + " whose file " + StepText.Quoted + " starts with " + StepText.Quoted + @" beside an (\S+) LICENSE file$")]
  public void TreeWithHeaderBesideLicence(string tree, string file, string line, string family)
    => Assert.That(Identify(Path.Combine(this.HeaderTree(tree, file, line), "LICENSE")), Is.EqualTo(family));

  private string HeaderTree(string tree, string file, string line) {
    var folder = this.Tree(tree);
    Assert.That(File.ReadLines(Path.Combine(folder, file)).First(), Is.EqualTo(line));
    return folder;
  }

  /// <summary>Copies the tree and checks it holds nothing but a file without a header.</summary>
  [Given("^the tree " + StepText.Quoted + " containing only " + StepText.Quoted + " without header and without a licence file$")]
  public void TreeWithOrphan(string tree, string file) {
    var folder = this.Tree(tree);
    Assert.That(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(Path.GetFileName), Is.EqualTo(new[] { file }));
    Assert.That(LicenceDetector.Header(File.ReadAllText(Path.Combine(folder, file))), Is.Null);
  }

  /// <summary>Copies the tree and checks the file and the licence file beside it.</summary>
  [Given("^the tree " + StepText.Quoted + " containing " + StepText.Quoted + @" beside an (\S+) LICENSE file$")]
  public void TreeWithAsset(string tree, string file, string licence) {
    var folder = this.Tree(tree);
    Assert.That(File.Exists(Path.Combine(folder, file)), Is.True);
    Assert.That(Identify(Path.Combine(folder, "LICENSE")), Is.EqualTo(licence));
  }

  /// <summary>Writes a tree of one file whose header declares an expression.</summary>
  [Given("^a file whose header declares " + StepText.Quoted + "$")]
  public void FileWithHeader(string expression) {
    this.CopyFolder(Path.Combine(_root, "tooling", "licensing"), Path.Combine("tooling", "licensing"));
    workspace.Write("expression/f.c", $"// SPDX-License-Identifier: {expression}\nint f(void) {{ return 0; }}\n");
  }

  /// <summary>Scans the one-file tree in distribution mode.</summary>
  [When("^it is scanned in distribution mode$")]
  public void ScanInDistributionMode() => log.Add(Toolchain.Tooling(workspace.Path, "licenses", "scan", "--mode", "distribution", "--root", "expression"));

  /// <summary>Checks the status of the one file.</summary>
  [Then("^its status is " + StepText.Quoted + "$")]
  public void ItsStatusIs(string status) => this.EntryHas("f.c", null, null, status);

  /// <summary>Checks licence, source and status of a report entry.</summary>
  [Then("^the report entry for " + StepText.Quoted + " has licence " + StepText.Quoted + ", source " + StepText.Quoted + " and status " + StepText.Quoted + "$")]
  public void EntryHasAll(string path, string licence, string source, string status) => this.EntryHas(path, licence, source, status);

  /// <summary>Checks licence and status of a report entry.</summary>
  [Then("^the report entry for " + StepText.Quoted + " has licence " + StepText.Quoted + " and status " + StepText.Quoted + "$")]
  public void EntryHasLicenceAndStatus(string path, string licence, string status) => this.EntryHas(path, licence, null, status);

  /// <summary>Checks licence and source of a report entry.</summary>
  [Then("^the report entry for " + StepText.Quoted + " has licence " + StepText.Quoted + " and source " + StepText.Quoted + "$")]
  public void EntryHasLicenceAndSource(string path, string licence, string source) => this.EntryHas(path, licence, source, null);

  /// <summary>Checks the status of a report entry.</summary>
  [Then("^the report entry for " + StepText.Quoted + " has status " + StepText.Quoted + "$")]
  public void EntryHasStatus(string path, string status) => this.EntryHas(path, null, null, status);

  /// <summary>
  /// Checks the given fields of the report entry of a path: the entry whose path is the given one or,
  /// in a corpus report whose paths start with <c>owner/repo@commit/</c>, ends with it.
  /// </summary>
  private void EntryHas(string path, string? licence, string? source, string? status) {
    using var report = JsonDocument.Parse(File.ReadAllText(workspace.Resolve(LicenceReport.DefaultPath)));
    var entries = report.RootElement.EnumerateArray()
      .Where(entry => entry.GetProperty("path").GetString() is { } named && (named == path || named.EndsWith("/" + path, System.StringComparison.Ordinal)))
      .ToList();
    Assert.That(entries, Has.Count.EqualTo(1), log.Last.Output);
    var entry = entries[0];
    if (licence is not null)
      Assert.That(entry.GetProperty("licence").GetString(), Is.EqualTo(licence));
    if (source is not null)
      Assert.That(entry.GetProperty("source").GetString(), Is.EqualTo(source));
    if (status is not null)
      Assert.That(entry.GetProperty("status").GetString(), Is.EqualTo(status));
  }

}
