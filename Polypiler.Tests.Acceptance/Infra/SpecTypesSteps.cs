using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Spec;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/spec-types.feature</c>. The workspace stands in for the
/// repository: it holds the types schema, a copy of the valid fixture tree of
/// <c>tests/fixtures/spec/types/valid/</c> under the same relative path, or an invalid fixture placed at
/// <c>spec/types/&lt;family&gt;/&lt;fixture&gt;</c> beside the valid documents it names.
/// </summary>
[Binding, Scope(Feature = "Schema and validator for spec/types/")]
public sealed class SpecTypesSteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Valid = "tests/fixtures/spec/types/valid";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  /// <summary>Puts the schema where the tool looks for it from the workspace.</summary>
  [BeforeScenario]
  public void CopySchema() => workspace.Copy(Path.Combine(_root, "spec/schemas/types.schema.json"), "spec/schemas/types.schema.json");

  /// <summary>Copies a fixture tree to the same relative path in the workspace.</summary>
  [Given("^the spec tree " + StepText.Quoted + "$")]
  public void Tree(string tree) {
    var source = Path.Combine(_root, tree);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, $"{tree}/{Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/')}");
  }

  /// <summary>Copies the valid tree after checking the two documents against the description.</summary>
  [Given("^the spec documents " + StepText.Quoted + " and " + StepText.Quoted + @", both with bits (\d+) and layout sizeBytes (\d+), each listing the other in distinctFrom$")]
  public void DistinctPair(string first, string second, int bits, int sizeBytes) {
    this.Tree(Valid);
    var tree = SpecTree.Load(workspace.Resolve(Valid), workspace.Path);
    var a = tree.Documents.Single(document => document.Path == "types/" + first);
    var b = tree.Documents.Single(document => document.Path == "types/" + second);
    foreach (var (self, other) in new[] { (a, b), (b, a) }) {
      Assert.That(((long)self.Body!["bits"]!, (long)self.Body["layout"]!["sizeBytes"]!), Is.EqualTo(((long)bits, (long)sizeBytes)));
      Assert.That(self.Body["distinctFrom"]!.AsArray().Select(entry => (string?)entry), Is.EqualTo(new[] { other.Id }));
    }
  }

  /// <summary>Takes an ID out of the <c>distinctFrom</c> list of a document of the valid tree.</summary>
  [When("^I remove " + StepText.Quoted + " from the distinctFrom of " + StepText.Quoted + "$")]
  public void RemoveDistinct(string id, string path) {
    var file = workspace.Resolve($"{Valid}/types/{path}");
    var text = File.ReadAllText(file);
    var edited = text.Replace($"distinctFrom: [{id}]", "distinctFrom: []", StringComparison.Ordinal);
    Assert.That(edited, Is.Not.EqualTo(text));
    File.WriteAllText(file, edited);
  }

  /// <summary>
  /// Places an invalid fixture at <c>spec/types/&lt;family&gt;/</c> of its body (<c>integer</c> when it
  /// names none), with every document of the valid tree whose ID its body names.
  /// </summary>
  [Given("^the spec fixture " + StepText.Quoted + " with the valid documents it refers to$")]
  public void Fixture(string fixture) {
    var text = File.ReadAllText(Path.Combine(_root, fixture));
    var body = SpecDocument.Parse("types/fixture.yaml", text).Body!;
    var family = (string?)body["family"] ?? "integer";
    workspace.Write($"spec/types/{family}/{Path.GetFileName(fixture)}", text);
    var valid = Path.Combine(_root, Valid);
    foreach (var file in Directory.EnumerateFiles(valid, "*.yaml", SearchOption.AllDirectories))
      if (body.ToJsonString().Contains($"\"TYPES.{Path.GetFileNameWithoutExtension(file)}\"", StringComparison.Ordinal))
        workspace.Copy(file, "spec/" + Path.GetRelativePath(valid, file).Replace(Path.DirectorySeparatorChar, '/'));
  }

  /// <summary>Checks the last line of standard output.</summary>
  [Then("^the last output line is " + StepText.Quoted + "$")]
  public void LastOutputLine(string text)
    => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stdout).Split('\n')[^1].TrimEnd('\r'), Is.EqualTo(StepText.Unescape(text)), log.Last.Output);

}
