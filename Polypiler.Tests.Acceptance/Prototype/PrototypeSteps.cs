using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// What the steps of one prototype scenario share: the source file in the workspace and the fixture
/// it came from, and translations of it through the scenario's <see cref="PolypilerRunner"/>.
/// </summary>
public sealed class PrototypeScenario(ScenarioWorkspace workspace, PolypilerRunner polypiler, CommandLog log) {

  /// <summary>The workspace of the scenario.</summary>
  public ScenarioWorkspace Workspace => workspace;

  /// <summary>The runner of the scenario.</summary>
  public PolypilerRunner Polypiler => polypiler;

  /// <summary>The command lines run so far.</summary>
  public CommandLog Log => log;

  /// <summary>The source file of the scenario, relative to the workspace.</summary>
  public string Source { get; set; } = "";

  /// <summary>The fixture the source was copied from, as <see cref="PrototypeFixture"/> names it (<c>wide/Crc32Table</c>); empty for a source of the scenario's own.</summary>
  public string Fixture { get; set; } = "";

  /// <summary>The text of the C# method or JavaScript function the scenario gave.</summary>
  public string Text { get; set; } = "";

  /// <summary>
  /// Writes a C# source of methods into the workspace as <c>Snippet.cs</c> and makes it the source. A
  /// method that calls <c>AsSpan</c> stands in a file with <c>using System;</c>, which that extension
  /// method needs.
  /// </summary>
  public string Snippet(string methods) {
    this.Text = methods;
    workspace.Write("Snippet.cs", (methods.Contains(".AsSpan(", StringComparison.Ordinal) ? "using System; " : "") + PrototypeFixture.Wrap(methods));
    return this.Source = "Snippet.cs";
  }

  /// <summary>Writes a source of the scenario's own into the workspace and makes it the source.</summary>
  public string Script(string name, string text) {
    workspace.Write(name, text);
    return this.Source = name;
  }

  /// <summary>
  /// Translates a file of the workspace into a directory of its own and returns the path and text of
  /// the generated file; a translation that fails fails the step.
  /// </summary>
  public (string Path, string Text) Translate(string source, string from, string to, params string[] further) {
    var output = "out-" + Guid.NewGuid().ToString("N");
    var result = polypiler.Run(["translate", source, "--from", from, "--to", to, "--output", output, .. further]);
    Assert.That(result.ExitCode, Is.Zero, result.Output);
    var file = Directory.GetFiles(workspace.Resolve(output)).Single(path => Path.GetFileName(path) != "polypiler.manifest.json");
    return (file, File.ReadAllText(file));
  }

}

/// <summary>The steps every prototype feature shares: a fixture or a C# method as the source of the scenario.</summary>
[Binding]
public sealed class PrototypeSteps(PrototypeScenario scenario) {

  /// <summary>
  /// Copies a fixture of <c>tests/fixtures/prototype</c> into the workspace under the same relative
  /// path; a name that is not there is looked up in the JavaScript fixtures of <c>js/</c>.
  /// </summary>
  [Given("^the fixture " + StepText.Quoted + "$")]
  public void Fixture(string path) {
    var root = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype");
    var source = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(source))
      source = Path.Combine(root, "js", path.Replace('/', Path.DirectorySeparatorChar));

    Assert.That(File.Exists(source), Is.True, $"no fixture {path}");
    scenario.Workspace.Copy(source, path);
    scenario.Source = path;
    scenario.Fixture = Path.GetExtension(path) == ".cs" ? path[..^3] : "";
  }

  /// <summary>Writes a C# method into the class <c>Snippet</c> of <c>Snippet.cs</c>.</summary>
  [Given("^the C# method " + StepText.Quoted + "$")]
  public void CSharpMethod(string method) => scenario.Snippet(StepText.Unescape(method));

}
