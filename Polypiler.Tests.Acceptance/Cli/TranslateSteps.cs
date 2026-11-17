using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Cli;

/// <summary>
/// The steps of <c>tests/features/cli/cli-translate.feature</c> (#180): the fixtures copied into the
/// workspace under their repository path, the manifest's state and canonical command line, and the
/// generated module run under the real <c>node</c>.
/// </summary>
[Binding, Scope(Feature = "polypiler translate")]
public sealed class TranslateSteps(ScenarioWorkspace workspace, CommandLog log) {

  private string _recorded = "";

  /// <summary>Copies a folder of fixtures into the workspace under the same relative path.</summary>
  [Given("^the fixtures of " + StepText.Quoted + "$")]
  public void TheFixturesOf(string folder) {
    foreach (var file in Directory.GetFiles(Path.Combine(PrototypeFixture.RepositoryRoot, folder)))
      workspace.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
  }

  /// <summary>Checks the last line of standard output.</summary>
  [Then("^the last line of stdout is " + StepText.Quoted + "$")]
  public void LastStdoutLineIs(string text)
    => Assert.That(log.Last.Stdout.TrimEnd('\n', '\r').Split('\n')[^1].TrimEnd('\r'), Is.EqualTo(StepText.Unescape(text)), log.Last.Output);

  /// <summary>Checks a member of a manifest, its path dotted.</summary>
  [Then("^" + StepText.Quoted + " has " + StepText.Quoted + " equal to " + StepText.Quoted + "$")]
  public void ManifestHas(string manifest, string member, string value)
    => Assert.That(Member(manifest, member), Is.EqualTo(value), log.Last.Output);

  /// <summary>Checks that every command line of the scenario ended with an exit code.</summary>
  [Then(@"^every exit code is (\d+)$")]
  public void EveryExitCodeIs(int expected)
    => Assert.That(log.Commands.Select(command => command.ExitCode), Is.All.EqualTo(expected), string.Join("\n", log.Commands.Select(command => command.Output)));

  /// <summary>Compares a member of two manifests and keeps it for the following steps.</summary>
  [Then("^" + StepText.Quoted + " of " + StepText.Quoted + " equals that of " + StepText.Quoted + "$")]
  public void MemberEquals(string member, string first, string second) {
    this._recorded = Member(first, member);
    Assert.That(Member(second, member), Is.EqualTo(this._recorded));
  }

  /// <summary>Checks the start of the kept member.</summary>
  [Then("^it starts with " + StepText.Quoted + "$")]
  public void ItStartsWith(string text) => Assert.That(this._recorded, Does.StartWith(StepText.Unescape(text)));

  /// <summary>Checks the end of the kept member.</summary>
  [Then("^it ends with " + StepText.Quoted + "$")]
  public void ItEndsWith(string text) => Assert.That(this._recorded, Does.EndWith(StepText.Unescape(text)));

  /// <summary>Checks that the kept member contains none of two texts.</summary>
  [Then("^it contains none of " + StepText.Quoted + ", " + StepText.Quoted + "$")]
  public void ItContainsNoneOf(string first, string second)
    => Assert.That(this._recorded, Does.Not.Contain(first).And.Not.Contain(second));

  /// <summary>Checks that a directory was not created.</summary>
  [Then("^the directory " + StepText.Quoted + " does not exist$")]
  public void DirectoryDoesNotExist(string path) => Assert.That(Directory.Exists(workspace.Resolve(path)), Is.False);

  /// <summary>Imports a generated module under <c>node</c> and calls one of its functions.</summary>
  [Then("^running " + StepText.Quoted + @" under node gives (\w+)\((\d+), (\d+)\) = (\d+)$")]
  public void NodeGives(string module, string function, string a, string b, string expected) {
    var script = workspace.Write("call.mjs", $"import {{ {function} }} from \"./{module}\";\nconsole.log(String({function}({a}, {b})));\n");
    var (exitCode, output) = Toolchain.RunNode(workspace.Path, script);
    Assert.That((exitCode, output.Trim()), Is.EqualTo((0, expected)));
  }

  private string Member(string manifest, string member) {
    JsonNode? node = JsonNode.Parse(File.ReadAllText(workspace.Resolve(manifest)));
    foreach (var name in member.Split('.'))
      node = node?[name];

    return node?.GetValue<string>() ?? "";
  }

}
