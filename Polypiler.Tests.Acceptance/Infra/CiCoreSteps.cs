using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Ci;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/infra-ci-core.feature</c>. The repository's own audit runs the
/// real <c>polypiler-tooling</c> in the repository, which needs the Release build of both test projects;
/// a fixture project or audit tree is copied into the scenario's workspace under the same relative
/// path, and the .NET SDK builds or formats the copy there.
/// </summary>
[Binding, Scope(Feature = "Core CI layer")]
public sealed class CiCoreSteps(ScenarioWorkspace workspace, CommandLog log) {

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private string _project = "";
  private string _source = "";
  private byte[] _hash = [];

  /// <summary>Runs a command line of <c>polypiler-tooling</c> with the repository as the current directory.</summary>
  [When("^I run " + StepText.Quoted + " in the repository$")]
  public void RunInRepository(string line) {
    var (target, arguments) = CommandSteps.Route([.. CommandLine.Split(line)]);
    Assert.That(target, Is.EqualTo(CommandTarget.Tooling), line);
    log.Add(Toolchain.Tooling(_root, arguments));
  }

  /// <summary>Checks the last line of standard output and that one line per layer file comes before it.</summary>
  [Then("^the output ends with " + StepText.Quoted + " preceded by one line per layer file$")]
  public void EndsWithAfterLayers(string last) {
    var lines = CommandSteps.WithoutTrailingNewline(log.Last.Stdout).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
    var layers = CiLayer.Files(_root).Select(Path.GetFileNameWithoutExtension).ToArray();
    Assert.That(lines[^1], Is.EqualTo(StepText.Unescape(last)), log.Last.Output);
    Assert.That(lines[..^1].Select(line => line.Split(':')[0]), Is.EqualTo(layers.Select(layer => $"layer {layer}")), log.Last.Output);
  }

  /// <summary>Copies the folder of a fixture project into the workspace and checks a text of its sources.</summary>
  [Given("^the project " + StepText.Quoted + " containing " + StepText.Quoted + "$")]
  public void ProjectContaining(string project, string text) {
    this.CopyProject(project);
    Assert.That(File.ReadAllText(this._source), Does.Contain(StepText.Unescape(text)));
  }

  /// <summary>Copies the folder of a fixture project into the workspace and checks that its class body is indented by four spaces.</summary>
  [Given("^the project " + StepText.Quoted + " with a 4-space-indented class body$")]
  public void ProjectIndentedByFour(string project) {
    this.CopyProject(project);
    Assert.That(File.ReadAllLines(this._source), Has.Some.Match(@"^    public static"));
    this._hash = SHA256.HashData(File.ReadAllBytes(this._source));
  }

  /// <summary>Runs a command of the .NET SDK on the project of the scenario, in the project's folder.</summary>
  [When("^\"dotnet (build|format)\" runs on it with " + StepText.Quoted + "$")]
  public void RunSdk(string command, string option) {
    var start = new ProcessStartInfo("dotnet") {
      WorkingDirectory = Path.GetDirectoryName(this._project)!,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    foreach (var argument in new[] { command, Path.GetFileName(this._project), option }.Concat(command == "build" ? ["-nodeReuse:false", "-p:UseSharedCompilation=false"] : []))
      start.ArgumentList.Add(argument);

    // The test host's own MSBuild settings must not leak into the SDK the scenario starts.
    foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)).ToList())
      start.Environment.Remove(name);
    start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
    start.Environment["DOTNET_NOLOGO"] = "1";
    using var process = Process.Start(start)!;
    var error = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    log.Add(new CommandResult(process.ExitCode, output, error.Result));
  }

  /// <summary>Checks that the last command failed.</summary>
  [Then("^the exit code is not 0$")]
  public void ExitCodeIsNotZero() => Assert.That(log.Last.ExitCode, Is.Not.Zero, log.Last.Output);

  /// <summary>Checks the two streams of the last command together.</summary>
  [Then("^the output contains " + StepText.Quoted + "$")]
  public void OutputContains(string text) => Assert.That(log.Last.Output, Does.Contain(StepText.Unescape(text)));

  /// <summary>Checks that the source of the project has the bytes it had before the command.</summary>
  [Then("^the file on disk is unchanged$")]
  public void FileUnchanged() => Assert.That(SHA256.HashData(File.ReadAllBytes(this._source)), Is.EqualTo(this._hash));

  /// <summary>Copies an audit tree into the workspace under the same relative path.</summary>
  [Given("^the audit tree " + StepText.Quoted + "$")]
  public void AuditTree(string tree) => this.CopyFolder(tree);

  /// <summary>Writes layer files of the given names; the first runs always, the others when a path matches their glob.</summary>
  [Given("^layer files " + StepText.Quoted + @" \(always\), " + StepText.Quoted + @" \(paths " + StepText.Quoted + @"\) and " + StepText.Quoted + @" \(paths " + StepText.Quoted + @"\)$")]
  public void LayerFiles(string always, string first, string firstPaths, string second, string secondPaths) {
    this.Layer(always, "**", true);
    this.Layer(first, firstPaths, false);
    this.Layer(second, secondPaths, false);
  }

  /// <summary>Writes <c>changed.txt</c> with two paths, one per line.</summary>
  [Given("^the changed file list " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void ChangedFiles(string first, string second) => workspace.Write("changed.txt", $"{first}\n{second}\n");

  /// <summary>Checks standard output exactly, after one trailing line break is taken off.</summary>
  [Then("^the output is '(.*)'$")]
  public void OutputIs(string text) => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stdout), Is.EqualTo(text), log.Last.Output);

  private void Layer(string name, string glob, bool always)
    => workspace.Write($"{CiLayer.Folder}/{name}.yaml", $"schema: 1\nlayer: {name}\ntoolchains: [dotnet-sdk]\nos: [ubuntu-latest]\npaths: [\"{glob}\"]\nalways: {(always ? "true" : "false")}\n");

  private void CopyProject(string project) {
    var folder = Path.GetDirectoryName(project)!.Replace('\\', '/');
    this.CopyFolder(folder);
    this._project = workspace.Resolve(project);
    this._source = Directory.GetFiles(Path.GetDirectoryName(this._project)!, "*.cs").Single();
  }

  private void CopyFolder(string folder) {
    var source = Path.Combine(_root, folder);
    foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, $"{folder}/{Path.GetRelativePath(source, file).Replace('\\', '/')}");
  }

}
