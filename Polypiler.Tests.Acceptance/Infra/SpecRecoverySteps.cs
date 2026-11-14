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
/// The steps of <c>tests/features/infra/spec-recovery.feature</c>. The scenario's workspace stands in
/// for the repository: it holds the recovery family's schema, and either a fixture tree of
/// <c>tests/fixtures/spec/recovery/</c> under the same relative path or a <c>spec/recovery/</c> built
/// from one fixture document, which the real <c>polypiler-tooling</c> executable then validates.
/// </summary>
[Binding, Scope(Feature = "Schema and validator for spec/recovery/")]
public sealed class SpecRecoverySteps(ScenarioWorkspace workspace, CommandLog log, CommandSteps commands) {

  private const string Fixtures = "tests/fixtures/spec/recovery";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private string _fixture = "";

  /// <summary>Puts the family's schema where the tool looks for it from the workspace.</summary>
  [BeforeScenario]
  public void CopySchema() => workspace.Copy(Path.Combine(_root, "spec/schemas/recovery.schema.json"), "spec/schemas/recovery.schema.json");

  /// <summary>Copies a fixture tree into the workspace under its repository path.</summary>
  [Given("^the spec tree " + StepText.Quoted + "$")]
  public void FixtureTree(string tree) {
    var source = Path.Combine(_root, tree);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, $"{tree}/{Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/')}");
  }

  /// <summary>Writes the valid document to <c>spec/recovery/</c> with its <c>proofMode</c> replaced, after checking it then has no <c>speculation</c>.</summary>
  [Given("^the valid document " + StepText.Quoted + " with proofMode changed to " + StepText.Quoted + " and no " + StepText.Quoted + " property$")]
  public void ChangedValidDocument(string name, string proofMode, string property) {
    var text = File.ReadAllText(Path.Combine(_root, Fixtures, "valid", "recovery", name));
    var changed = text.Replace("  proofMode: proven\n", $"  proofMode: {proofMode}\n", System.StringComparison.Ordinal);
    Assert.That(changed, Is.Not.EqualTo(text));
    Assert.That(changed, Does.Not.Contain($"  {property}:"));
    workspace.Write($"spec/recovery/{name}", changed);
  }

  /// <summary>Remembers a fixture document of <c>tests/fixtures/spec/recovery/invalid/</c>.</summary>
  [Given("^the spec fixture " + StepText.Quoted + "$")]
  public void Fixture(string path) {
    Assert.That(File.Exists(Path.Combine(_root, path)), Is.True, path);
    this._fixture = path;
  }

  /// <summary>
  /// Writes the remembered fixture as the only document of <c>spec/recovery/</c>, named after its
  /// <c>optimization</c> (or, where it lacks one, its ID) as the layout asks, and runs the command line.
  /// A recovery body names no other document, so the fixture is all the tree needs.
  /// </summary>
  [When(@"^I run ""(.*)"" on a tree containing only that fixture and the valid documents it refers to$")]
  public void RunOnFixture(string line) {
    var text = File.ReadAllText(Path.Combine(_root, this._fixture));
    var document = SpecDocument.Parse("recovery/fixture.yaml", text);
    var name = (string?)document.Body?["optimization"] ?? document.Id![(document.Id!.IndexOf('.') + 1)..];
    workspace.Write($"spec/recovery/{name}.yaml", text);
    commands.Run(line);
  }

  /// <summary>Checks the last line of standard output.</summary>
  [Then("^the last output line is " + StepText.Quoted + "$")]
  public void LastOutputLine(string text)
    => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stdout).Split('\n').Last().TrimEnd('\r'), Is.EqualTo(StepText.Unescape(text)), log.Last.Output);

}
