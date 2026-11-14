using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Spec;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/spec-verification.feature</c>. The scenario's workspace stands in
/// for the repository: it holds the schemas of the families of the valid tree, and the real <c>polypiler-tooling</c> executable
/// validates the documents copied into it. A fixture of <c>tests/fixtures/spec/verification/invalid/</c>
/// is placed where its ID <c>VERIFICATION.&lt;kind&gt;.&lt;name&gt;</c> says it lives,
/// <c>spec/verification/&lt;kind&gt;s/&lt;name&gt;.yaml</c>, beside the documents of
/// <c>tests/fixtures/spec/verification/valid/</c> it names — and, for a tolerance, the valid tolerance of
/// the same operation — so the one defect it carries is the only one.
/// </summary>
[Binding, Scope(Feature = "Schema and validator for spec/verification/")]
public sealed class SpecVerificationSteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Valid = "tests/fixtures/spec/verification/valid";

  private const string Invalid = "tests/fixtures/spec/verification/invalid";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private string? _fixture;

  /// <summary>Puts the schemas of the families of the valid tree where the tool looks for them from the workspace.</summary>
  [BeforeScenario]
  public void CopySchemas() {
    foreach (var family in new[] { "verification", "intrinsics", "types", "ir" })
      workspace.Copy(Path.Combine(_root, "spec", "schemas", $"{family}.schema.json"), $"spec/schemas/{family}.schema.json");
  }

  /// <summary>The place under the spec root that a fixture's ID gives it.</summary>
  public static string PlaceOf(string text) {
    var parts = SpecDocument.Parse("verification/fixture.yaml", text).Id!.Split('.', 3);
    return $"verification/{parts[1]}s/{parts[2]}.yaml";
  }

  /// <summary>Copies a fixture tree of the repository into the workspace under the same relative path.</summary>
  [Given("^the spec tree " + StepText.Quoted + "$")]
  public void Tree(string tree) {
    var source = Path.Combine(_root, tree);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, $"{tree}/{Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/')}");
  }

  /// <summary>Places the invalid fixture that lives at the path, after checking its generator against the description.</summary>
  [Given("^the spec document " + StepText.Quoted + " with type " + StepText.Quoted + ", strategy " + StepText.Quoted + @", count (\d+) and no seed$")]
  public void Generator(string path, string type, string strategy, int count) {
    var text = Directory.EnumerateFiles(Path.Combine(_root, Invalid), "*.yaml").Select(File.ReadAllText).Single(candidate => PlaceOf(candidate) == "verification/" + path);
    var body = SpecDocument.Parse("verification/" + path, text).Body!;
    Assert.That(((string?)body["type"], (string?)body["strategy"], (long)body["count"]!, body["seed"]), Is.EqualTo((type, strategy, (long)count, (JsonNode?)null)));
    this.Place(text);
  }

  /// <summary>Remembers a fixture of the repository.</summary>
  [Given("^the spec fixture " + StepText.Quoted + "$")]
  public void Fixture(string path) {
    Assert.That(File.Exists(Path.Combine(_root, path)), Is.True, path);
    this._fixture = path;
  }

  /// <summary>Places the remembered fixture beside the valid documents it refers to and runs the command line there.</summary>
  [When("^I run " + StepText.Quoted + " on a tree containing only that fixture and the valid documents it refers to$")]
  public void RunOnFixture(string line) {
    this.Place(File.ReadAllText(Path.Combine(_root, this._fixture!)));
    var words = CommandLine.Split(line).ToArray();
    Assert.That(words[0], Is.EqualTo("polypiler-tooling"));
    log.Add(Toolchain.Tooling(workspace.Path, words[1..]));
  }

  /// <summary>Checks the last line of standard output.</summary>
  [Then("^the last output line is " + StepText.Quoted + "$")]
  public void LastOutputLine(string text)
    => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stdout).Split('\n')[^1].TrimEnd('\r'), Is.EqualTo(StepText.Unescape(text)), log.Last.Output);

  /// <summary>Writes a fixture at its place, with every valid document whose ID its body names or that is a tolerance of its operation.</summary>
  private void Place(string text) {
    var fixture = SpecDocument.Parse("verification/fixture.yaml", text);
    var named = fixture.Body!.ToJsonString();
    var operation = (string?)fixture.Body["operation"];
    workspace.Write("spec/" + PlaceOf(text), text);
    foreach (var document in SpecTree.Load(Path.Combine(_root, Valid), _root).Documents)
      if (named.Contains($"\"{document.Id}\"", System.StringComparison.Ordinal)
          || (operation is not null && (string?)document.Body!["operation"] == operation))
        workspace.Copy(Path.Combine(_root, Valid, document.Path), "spec/" + document.Path);
  }

}
