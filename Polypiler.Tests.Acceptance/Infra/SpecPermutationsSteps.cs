using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Spec;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/spec-permutations.feature</c>. The scenario's workspace stands
/// in for the repository: it holds the family's schema, and the real <c>polypiler-tooling</c>
/// executable validates the documents copied into it. A fixture of
/// <c>tests/fixtures/spec/permutations/invalid/</c> is placed where its ID
/// <c>PERMUTATIONS.&lt;kind&gt;.&lt;name&gt;</c> says it lives, <c>spec/permutations/&lt;kind&gt;s/&lt;name&gt;.yaml</c>,
/// so the one defect it carries is the only one.
/// </summary>
[Binding, Scope(Feature = "Schema and validator for spec/permutations/")]
public sealed class SpecPermutationsSteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Invalid = "tests/fixtures/spec/permutations/invalid";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private string? _fixture;

  /// <summary>Puts the family's schema where the tool looks for it from the workspace.</summary>
  [BeforeScenario]
  public void CopySchema() => workspace.Copy(Path.Combine(_root, "spec", "schemas", "permutations.schema.json"), "spec/schemas/permutations.schema.json");

  /// <summary>The place under the spec root that a fixture's ID gives it.</summary>
  public static string PlaceOf(string text) {
    var parts = SpecDocument.Parse("permutations/fixture.yaml", text).Id!.Split('.', 3);
    return $"permutations/{parts[1]}s/{parts[2]}.yaml";
  }

  /// <summary>Copies a fixture tree of the repository into the workspace under the same relative path.</summary>
  [Given("^the spec tree " + StepText.Quoted + "$")]
  public void Tree(string tree) {
    var source = Path.Combine(_root, tree);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, $"{tree}/{Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/')}");
  }

  /// <summary>Places the invalid fixture that lives at the path, after checking its mapping against the description.</summary>
  [Given("^the spec document " + StepText.Quoted + @" with inputWidth (\d+), outputs \[([^\]]*)\] and bijective (true|false)$")]
  public void Mapping(string path, int width, string outputs, bool bijective) {
    var text = Directory.EnumerateFiles(Path.Combine(_root, Invalid), "*.yaml").Select(File.ReadAllText).Single(candidate => PlaceOf(candidate) == "permutations/" + path);
    var body = SpecDocument.Parse("permutations/" + path, text).Body!;
    Assert.That(((long)body["inputWidth"]!, string.Join(", ", body["outputs"]!.AsArray().Select(entry => entry!.ToJsonString())), (bool)body["bijective"]!),
      Is.EqualTo(((long)width, outputs, bijective)));
    workspace.Write("spec/permutations/" + path, text);
  }

  /// <summary>Remembers a fixture of the repository.</summary>
  [Given("^the spec fixture " + StepText.Quoted + "$")]
  public void Fixture(string path) {
    Assert.That(File.Exists(Path.Combine(_root, path)), Is.True, path);
    this._fixture = path;
  }

  /// <summary>Places the remembered fixture in an otherwise empty spec tree and runs the command line there. The fixtures name no other document.</summary>
  [When(@"^I run ""(.*)"" on a tree containing only that fixture and the valid documents it refers to$")]
  public void RunOnFixture(string line) {
    var text = File.ReadAllText(Path.Combine(_root, this._fixture!));
    Assert.That(SpecDocument.Parse("permutations/fixture.yaml", text).Body!["target"], Is.Null, "a fixture that names another document needs that document in the tree");
    workspace.Write("spec/" + PlaceOf(text), text);
    var words = CommandLine.Split(line).ToArray();
    Assert.That(words[0], Is.EqualTo("polypiler-tooling"));
    log.Add(Toolchain.Tooling(workspace.Path, words[1..]));
  }

  /// <summary>Checks the last line of standard output.</summary>
  [Then("^the last output line is " + StepText.Quoted + "$")]
  public void LastOutputLine(string text)
    => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stdout).Split('\n')[^1].TrimEnd('\r'), Is.EqualTo(StepText.Unescape(text)), log.Last.Output);

}
