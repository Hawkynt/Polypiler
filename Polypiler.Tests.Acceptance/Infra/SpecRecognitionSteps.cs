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
/// The steps of <c>tests/features/infra/spec-recognition.feature</c>. The scenario's workspace stands in
/// for the repository: it holds the recognition family's schema, and either the valid fixture tree of
/// <c>tests/fixtures/spec/recognition/</c> under the same relative path or a <c>spec/</c> tree of one
/// recognition document and the intrinsic it introduces, which the real <c>polypiler-tooling</c>
/// executable then validates.
/// </summary>
[Binding, Scope(Feature = "Schema and validator for spec/recognition/")]
public sealed class SpecRecognitionSteps(ScenarioWorkspace workspace, CommandLog log, CommandSteps commands) {

  private const string Fixtures = "tests/fixtures/spec/recognition";

  private const string RotateLeft = "intrinsics/shuffle/rotateleft.yaml";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private string _fixture = "";

  /// <summary>Puts the family's schema where the tool looks for it from the workspace.</summary>
  [BeforeScenario]
  public void CopySchema() => workspace.Copy(Path.Combine(_root, "spec/schemas/recognition.schema.json"), "spec/schemas/recognition.schema.json");

  /// <summary>Copies a fixture tree into the workspace under its repository path.</summary>
  [Given("^the spec tree " + StepText.Quoted + "$")]
  public void FixtureTree(string tree) {
    var source = Path.Combine(_root, tree);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, $"{tree}/{Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/')}");
  }

  /// <summary>Writes the valid document to <c>spec/recognition/</c> with one of its lists replaced by an empty one.</summary>
  [Given("^the valid document " + StepText.Quoted + " with its " + StepText.Quoted + " list emptied$")]
  public void EmptiedList(string name, string list) {
    var text = File.ReadAllText(Path.Combine(_root, Fixtures, "valid", "recognition", name));
    var lines = text.Split('\n').ToList();
    var start = lines.IndexOf($"  {list}:");
    Assert.That(start, Is.GreaterThan(0), list);
    var end = start + 1;
    while (lines[end].StartsWith("    ", System.StringComparison.Ordinal))
      ++end;
    Assert.That(end, Is.GreaterThan(start + 1), list + " holds entries");
    lines.RemoveRange(start, end - start);
    lines.Insert(start, $"  {list}: []");
    this.WriteTree($"spec/recognition/{name}", string.Join('\n', lines));
  }

  /// <summary>Remembers a fixture document of <c>tests/fixtures/spec/recognition/invalid/</c>.</summary>
  [Given("^the spec fixture " + StepText.Quoted + "$")]
  public void Fixture(string path) {
    Assert.That(File.Exists(Path.Combine(_root, path)), Is.True, path);
    this._fixture = path;
  }

  /// <summary>
  /// Writes the remembered fixture as the only document of <c>spec/recognition/</c>, named after its
  /// <c>optimization</c> (or, where it lacks one, its ID) as the layout asks, with the intrinsic the
  /// valid document introduces, and runs the command line.
  /// </summary>
  [When(@"^I run ""(.*)"" on a tree containing only that fixture and the valid documents it refers to$")]
  public void RunOnFixture(string line) {
    var text = File.ReadAllText(Path.Combine(_root, this._fixture));
    var document = SpecDocument.Parse("recognition/fixture.yaml", text);
    var name = (string?)document.Body?["optimization"] ?? document.Id![(document.Id!.IndexOf('.') + 1)..];
    this.WriteTree($"spec/recognition/{name}.yaml", text);
    commands.Run(line);
  }

  /// <summary>Checks the last line of standard output.</summary>
  [Then("^the last output line is " + StepText.Quoted + "$")]
  public void LastOutputLine(string text)
    => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stdout).Split('\n').Last().TrimEnd('\r'), Is.EqualTo(StepText.Unescape(text)), log.Last.Output);

  /// <summary>Writes a recognition document and the intrinsic the valid document introduces.</summary>
  private void WriteTree(string path, string text) {
    workspace.Copy(Path.Combine(_root, Fixtures, "valid", RotateLeft), "spec/" + RotateLeft);
    workspace.Write(path, text);
  }

}
