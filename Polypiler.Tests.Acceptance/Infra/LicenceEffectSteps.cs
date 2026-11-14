using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Tooling.Licensing;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Licensing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/infra-license-effect.feature</c>. The closures are computed in
/// process with the shipped policy; every other scenario's workspace holds a copy of the repository's
/// <c>tooling/licensing</c> and <c>tooling/corpus/corpus.yaml</c>, so the real <c>polypiler-tooling</c>
/// executable finds the policy and the pinned repositories there, and a tree named by a step is copied
/// from <c>tests/fixtures/infra/license-effect</c> under its own name, a package tree with a manifest
/// from the real writer (<see cref="LicenceEffectTrees"/>).
/// </summary>
[Binding, Scope(Feature = "Effective licence and the GPL fence")]
public sealed class LicenceEffectSteps(ScenarioWorkspace workspace, CommandLog log) {

  private static readonly string _root = PrototypeFixture.RepositoryRoot;
  private static readonly Regex _quoted = new("\"([^\"]*)\"", RegexOptions.CultureInvariant);

  private string[] _closure = [];
  private EffectiveLicenceResult? _result;

  private static LicencePolicy Shipped { get; } = LicencePolicy.Load(Path.Combine(_root, LicencePolicy.RepositoryPath));

  /// <summary>Reads the closure: every quoted licence of the cell.</summary>
  [Given("^a closure with the licences (.+)$")]
  public void AClosure(string licences) => this._closure = [.. _quoted.Matches(licences).Select(match => match.Groups[1].Value)];

  /// <summary>Computes the effective licence of the closure; nothing may be reported.</summary>
  [When("^the effective licence is computed$")]
  public void Compute() {
    var bag = new DiagnosticBag();
    this._result = EffectiveLicence.Compute(this._closure.Select((licence, index) => new LicensedSource($"member{index}", licence)), Shipped, bag);
    Assert.That(bag.Items, Is.Empty);
  }

  /// <summary>Checks the label.</summary>
  [Then("^it is " + StepText.Quoted + "$")]
  public void ItIs(string licence) => Assert.That(this._result?.Licence, Is.EqualTo(licence));

  private void Repository() {
    var licensing = Path.Combine(_root, "tooling", "licensing");
    foreach (var file in Directory.EnumerateFiles(licensing, "*", SearchOption.AllDirectories))
      workspace.Copy(file, Path.Combine("tooling", "licensing", Path.GetRelativePath(licensing, file)));
    workspace.Copy(Path.Combine(_root, CorpusManifest.RepositoryPath), CorpusManifest.RepositoryPath);
  }

  private Manifest Package(string tree) {
    this.Repository();
    LicenceEffectTrees.Package(tree, workspace.Resolve(tree));
    return ManifestReader.Read(workspace.Resolve(Path.Combine(tree, Manifest.FileName)), new DiagnosticBag())!;
  }

  /// <summary>Copies a package tree whose manifest lists one local input and states a licence, and checks the input's licence file.</summary>
  [Given("^the package tree " + StepText.Quoted + " whose manifest lists the input " + StepText.Quoted + @" \(licence (\S+) by its nearest licence file\) and states outputLicence " + StepText.Quoted + "$")]
  public void PackageWithLocalInput(string tree, string input, string licence, string stated) {
    var manifest = this.Package(tree);
    Assert.That((manifest.Inputs.Single().Path, manifest.Licensing.OutputLicence), Is.EqualTo((input, stated)));
    Assert.That(File.Exists(workspace.Resolve(Path.Combine(tree, input))), Is.True);
    Assert.That(LicenceDeclarations.Load(workspace.Resolve(tree)).For(input), Is.EqualTo(licence));
  }

  /// <summary>Copies a package tree with the input of the previous one and checks the licence it states.</summary>
  [Given("^the package tree " + StepText.Quoted + " with the same input and outputLicence " + StepText.Quoted + "$")]
  public void PackageWithTheSameInput(string tree, string stated) {
    var manifest = this.Package(tree);
    Assert.That((manifest.Inputs.Single().Path, manifest.Licensing.OutputLicence), Is.EqualTo(("gpl-nested/sub/x.c", stated)));
  }

  /// <summary>Copies a package tree whose manifest lists one pinned input of a repository and checks it.</summary>
  [Given("^the package tree " + StepText.Quoted + " whose manifest lists the input " + StepText.Quoted + " of " + StepText.Quoted + " and states outputLicence " + StepText.Quoted + "$")]
  public void PackageWithPinnedInput(string tree, string path, string repository, string stated) {
    var manifest = this.Package(tree);
    var input = manifest.Inputs.Single();
    Assert.That((input.Provenance?.Repository, input.Provenance?.Path, manifest.Licensing.OutputLicence), Is.EqualTo((repository, path, stated)));
  }

  /// <summary>Copies a package tree whose one input carries a licence header.</summary>
  [Given("^the package tree " + StepText.Quoted + " whose manifest lists an input under " + StepText.Quoted + "$")]
  public void PackageWithHeaderInput(string tree, string licence) {
    var input = this.Package(tree).Inputs.Single();
    Assert.That(LicenceDetector.Header(File.ReadAllText(workspace.Resolve(Path.Combine(tree, input.Path)))), Is.EqualTo(licence));
  }

  /// <summary>Copies a repository tree and checks the entry of its notice.</summary>
  [Given("^the repository tree " + StepText.Quoted + " whose folder " + StepText.Quoted + " has a " + StepText.Quoted + " entry with " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void RepositoryWithNoticeEntry(string tree, string project, string notice, string origin, string licence) {
    var entry = this.Notice(tree, project, notice).Single();
    Assert.That(($"{ProvenanceNotice.OriginKey} {entry.Origins.Single()}", $"{ProvenanceNotice.LicenceKey} {entry.Licences.Single()}"), Is.EqualTo((origin, licence)));
  }

  /// <summary>Copies a repository tree and checks the licences of the entries of its notice.</summary>
  [Given("^the repository tree " + StepText.Quoted + " whose folder " + StepText.Quoted + " has notice entries with licences " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void RepositoryWithNoticeEntries(string tree, string project, string first, string second)
    => Assert.That(this.Notice(tree, project, ProvenanceNotice.MarkdownName).Select(entry => entry.Licences.Single()), Is.EqualTo(new[] { first, second }));

  private System.Collections.Generic.IReadOnlyList<ProvenanceEntry> Notice(string tree, string project, string notice) {
    this.Repository();
    LicenceEffectTrees.Copy(tree, workspace.Resolve(tree));
    return ProvenanceNotice.Parse(notice, File.ReadAllText(workspace.Resolve(Path.Combine(tree, project, notice))));
  }

  /// <summary>Checks the effective licence and the members that decided it.</summary>
  [Then("^" + StepText.Quoted + " has effectiveLicence " + StepText.Quoted + @" and because \[(.*)\]$")]
  public void EffectiveWithBecause(string file, string licence, string because) {
    using var json = this.Effective(file, licence);
    Assert.That(json.RootElement.GetProperty("because").EnumerateArray().Select(origin => origin.GetString()), Is.EqualTo(_quoted.Matches(because).Select(match => match.Groups[1].Value)));
  }

  /// <summary>Checks the effective licence.</summary>
  [Then("^" + StepText.Quoted + " has effectiveLicence " + StepText.Quoted + "$")]
  public void EffectiveIs(string file, string licence) => this.Effective(file, licence).Dispose();

  private JsonDocument Effective(string file, string licence) {
    var json = JsonDocument.Parse(File.ReadAllText(workspace.Resolve(file)));
    Assert.That(json.RootElement.GetProperty("effectiveLicence").GetString(), Is.EqualTo(licence), log.Last.Output);
    return json;
  }

}
