using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Toolchains;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The steps of <c>tests/features/infra/infra-ci-js.feature</c>. Every command runs through
/// <see cref="JsCommands"/> in the sandbox of this host, on the fixture files the scenario names and
/// nothing else; its exit code and output go to the <see cref="CommandLog"/>, where the shared command
/// steps check them. A command names its toolchain by the executable: <c>node</c>, or <c>tsc</c> for
/// the key <c>typescript</c>. Before the first command of a toolchain runs, the toolchain is probed as
/// <see cref="RequiresToolchainAttribute"/> does.
/// </summary>
[Binding, Scope(Feature = "JS/TS CI layer")]
public sealed class JsLayerSteps(CommandLog log) {

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private readonly Dictionary<string, ReadOnlyMemory<byte>> _files = new(StringComparer.Ordinal);
  private readonly List<RunResult> _runs = [];

  private void Add(params string[] names) {
    foreach (var (name, bytes) in JsCommands.Fixtures(names))
      this._files[name] = bytes;
  }

  /// <summary>The name of a fixture of the layer, from its path relative to the repository.</summary>
  private static string Fixture(string path) {
    var full = Path.GetFullPath(Path.Combine(_root, path));
    Assert.That(Path.GetDirectoryName(full), Is.EqualTo(Path.TrimEndingDirectorySeparator(JsCommands.FixtureFolder)), path);
    return Path.GetFileName(full);
  }

  private static ProcessSpec Command(string text) {
    var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return words switch {
      ["node", "--test", var file] => JsCommands.Test(file),
      ["node", var file, .. var arguments] => JsCommands.Run(file, arguments),
      ["tsc", "--noEmit", "-p", var directory] => JsCommands.TypeCheck(directory),
      ["tsc", "--noEmit", var file] => JsCommands.TypeCheckFile(file),
      _ => throw new ArgumentException($"'{text}' is not a command of the JS/TS layer", nameof(text))
    };
  }

  private async Task RunAsync(string text) {
    var command = Command(text);
    new RequiresToolchainAttribute(command.Toolchain).Check(Environment.GetEnvironmentVariable(ToolchainLocator.RequireVariable));
    var result = await JsCommands.RunAsync(this._files, command);
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Completed), string.Join("\n", result.Diagnostics));
    this._runs.Add(result);
    log.Add(JsCommands.ToCommandResult(result));
  }

  [Given("^the module " + StepText.Quoted + " and its test " + StepText.Quoted + "$")]
  public void ModuleAndTest(string module, string test) => this.Add(Fixture(module), test);

  [Given("^the module " + StepText.Quoted + " printing " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void ModulePrinting(string module, string first, string second) {
    var name = Fixture(module);
    this.Add(name);
    var source = File.ReadAllText(Path.Combine(JsCommands.FixtureFolder, name));
    Assert.That(source, Does.Contain($"console.log{first};").And.Contain($"console.log{second};"));
  }

  [Given("^the project " + StepText.Quoted + " with " + StepText.Quoted + " and a strict " + StepText.Quoted + "$")]
  public void TypedProject(string directory, string source, string project) {
    Assert.That(Path.GetFullPath(Path.Combine(_root, directory)), Is.EqualTo(Path.TrimEndingDirectorySeparator(JsCommands.FixtureFolder)));
    this.Add(source, project);
    Assert.That(JsCommands.TypeScriptRecord(this._files), Is.EqualTo(JsCommands.Checked));
    Assert.That(File.ReadAllText(Path.Combine(JsCommands.FixtureFolder, project)), Does.Match("\"strict\":\\s*true"));
  }

  [Given("^the test " + StepText.Quoted + " asserting that 1 equals 2$")]
  public void FailingTest(string test) {
    var name = Fixture(test);
    this.Add(name);
    Assert.That(File.ReadAllText(Path.Combine(JsCommands.FixtureFolder, name)), Does.Contain("assert.equal(1, 2);"));
  }

  [Given("^the file " + StepText.Quoted + " containing " + StepText.Quoted + "$")]
  public void FileContaining(string file, string text) {
    var name = Fixture(file);
    this.Add(name);
    Assert.That(File.ReadAllText(Path.Combine(JsCommands.FixtureFolder, name)), Does.Contain(StepText.Unescape(text)));
  }

  [When("^I run " + StepText.Quoted + " and " + StepText.Quoted + " in the sandbox$")]
  public async Task RunTwo(string first, string second) {
    await this.RunAsync(first);
    await this.RunAsync(second);
  }

  [When("^I run " + StepText.Quoted + " in the sandbox$")]
  public Task RunOne(string command) => this.RunAsync(command);

  [Then("^the first command prints " + StepText.Quoted + "$")]
  public void FirstPrints(string text) {
    var first = log.Commands[0];
    Assert.That((first.ExitCode, CommandSteps.WithoutTrailingNewline(first.Stdout)), Is.EqualTo((0, text)), first.Output);
  }

  [Then("^the second command exits with (\\d+) and reports " + StepText.Quoted + "$")]
  public void SecondExitsAndReports(int exitCode, string text) {
    var second = log.Commands[1];
    Assert.That(second.ExitCode, Is.EqualTo(exitCode), second.Output);
    Assert.That(second.Output, Does.Match(@"\b" + text + @"\b"));
  }

  [Then("^stdout is " + StepText.Quoted + " followed by " + StepText.Quoted + "$")]
  public void StdoutIsTwoLines(string first, string second)
    => Assert.That(log.Last.Stdout.ReplaceLineEndings("\n"), Is.EqualTo($"{first}\n{second}\n"), log.Last.Output);

  [Then("^the output reports " + StepText.Quoted + "$")]
  public void OutputReports(string text) => Assert.That(log.Last.Output, Does.Match(@"\b" + text + @"\b"));

  [Then("^no " + StepText.Quoted + " file is written$")]
  public void NothingWritten(string extension) {
    var collected = this._runs[^1].Collected.Keys.ToList();
    Assert.That(collected, Is.EquivalentTo(this._files.Keys), "the run directory holds what the plan put there");
    Assert.That(collected, Has.None.EndsWith(extension));
  }

}
