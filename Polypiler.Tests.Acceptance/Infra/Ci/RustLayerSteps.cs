using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The steps of <c>tests/features/infra/infra-ci-rust.feature</c>. Each command runs on a fresh copy
/// of the crate in the runner's sandbox; its exit code and both streams go to the scenario's command
/// log, which the shared steps <c>the exit code is</c> and <c>stderr contains</c> read.
/// </summary>
[Binding, Scope(Feature = "Rust CI layer")]
public sealed class RustLayerSteps(CommandLog log) {

  private IReadOnlyDictionary<string, ReadOnlyMemory<byte>> _crate = new Dictionary<string, ReadOnlyMemory<byte>>();
  private readonly Dictionary<string, CommandResult> _byCommand = new(StringComparer.Ordinal);
  private bool? _miriRuns;
  private string _note = "";

  [Given("^the crate " + StepText.Quoted + "$")]
  public void Crate(string crate) => this._crate = RustLayerRun.Crate(crate);

  [Given("^the crate " + StepText.Quoted + " whose function ends in " + StepText.Quoted + "$")]
  public void CrateEndingIn(string crate, string text) {
    this.Crate(crate);
    Assert.That(this.Source("src/lib.rs"), Does.Contain(StepText.Unescape(text) + "\n}"));
  }

  [Given("^the crate " + StepText.Quoted + @" whose test calls get_unchecked\((\d+)\) on a slice of length (\d+)$")]
  public void CrateReadingPastTheEnd(string crate, int index, int length) {
    this.Crate(crate);
    var source = this.Source("src/lib.rs");
    Assert.That(source, Does.Contain("get_unchecked(index)"));
    Assert.That(source, Does.Contain($"[{string.Join(", ", Enumerable.Range(1, length))}]"));
    Assert.That(source, Does.Contain($"read_unchecked(&values, {index})"));
  }

  [When("^I run " + StepText.Quoted + ", " + StepText.Quoted + ", " + StepText.Quoted + " and " + StepText.Quoted + " in the sandbox$")]
  public async Task RunSmoke(string format, string clippy, string test, string run) {
    RustLayerRun.Require(RustCommands.Toolchain);
    var commands = RustLayerRun.Commands;
    string[] lines = [format, clippy, test, run];
    foreach (var (line, spec) in lines.Zip(commands.Smoke)) {
      Assert.That(Text(spec), Is.EqualTo(line), "the feature names the commands the layer runs");
      this._byCommand[line] = await this.RunAsync(spec);
    }
  }

  [When("^I run " + StepText.Quoted + " in the sandbox$")]
  public async Task RunOne(string line) {
    RustLayerRun.Require(RustCommands.Toolchain);
    var commands = RustLayerRun.Commands;
    var spec = new[] { commands.Format, commands.Clippy, commands.Test(), commands.Run }.Single(candidate => Text(candidate) == line);
    await this.RunAsync(spec);
  }

  [When("^I run \"cargo miri test\" with the " + StepText.Quoted + " toolchain in the sandbox$")]
  public async Task RunMiri(string toolchain) {
    Assert.That(toolchain, Is.EqualTo(RustCommands.MiriToolchain));
    RustLayerRun.Require(RustCommands.Toolchain);
    RustLayerRun.Require(RustCommands.MiriToolchain);
    Assert.That(RustCommands.NeedsMiri(this._crate), Is.True, "the crate contains unsafe code");
    await this.RunAsync(RustLayerRun.Commands.Miri());
  }

  [When("^the layer decides whether to run Miri$")]
  public void DecideMiri() {
    this._miriRuns = RustCommands.NeedsMiri(this._crate);
    if (this._miriRuns == false) {
      this._note = RustCommands.MiriNotApplicable;
      TestContext.Out.WriteLine(this._note);
    }
  }

  [Then("^every command exits with 0$")]
  public void EveryCommandSucceeds() {
    Assert.That(this._byCommand, Has.Count.EqualTo(4));
    Assert.That(this._byCommand.Values.Select(result => result.ExitCode), Is.All.Zero, string.Join("\n", this._byCommand.Values.Select(result => result.Output)));
  }

  [Then("^" + StepText.Quoted + " reports " + StepText.Quoted + "$")]
  public void Reports(string line, string text) {
    var output = this._byCommand[line].Stdout;
    Assert.That(output, Does.Contain(text));
    Assert.That(RustCommands.SmokeProblem(output), Is.Null);
  }

  [Then("^" + StepText.Quoted + " prints " + StepText.Quoted + "$")]
  public void Prints(string line, string text) => Assert.That(this._byCommand[line].Stdout, Is.EqualTo(text + "\n"));

  [Then("^stdout contains a diff starting with " + StepText.Quoted + "$")]
  public void DiffStartsWith(string text) {
    Assert.That(log.Last.Stdout, Does.StartWith(text), log.Last.Output);
    Assert.That(log.Last.Stdout, Does.Contain("\n-").And.Contain("\n+"));
  }

  [Then("^the exit code is not 0$")]
  public void ExitCodeIsNotZero() => Assert.That(log.Last.ExitCode, Is.Not.Zero, log.Last.Output);

  [Then("^Miri is not run$")]
  public void MiriIsNotRun() {
    Assert.That(this._miriRuns, Is.False);
    Assert.That(log.Commands, Is.Empty, "no command ran");
  }

  [Then("^the test output contains " + StepText.Quoted + "$")]
  public void TestOutputContains(string text) => Assert.That(this._note, Is.EqualTo(text));

  /// <summary>A command as the feature writes it: <c>cargo</c> and the arguments.</summary>
  private static string Text(ProcessSpec spec) => string.Join(' ', spec.Arguments.Prepend("cargo"));

  private string Source(string name) => Encoding.UTF8.GetString(this._crate[name].Span);

  private async Task<CommandResult> RunAsync(ProcessSpec spec) {
    var result = await RustLayerRun.RunAsync(this._crate, spec);
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Completed), string.Join("\n", result.Diagnostics.Select(diagnostic => $"{diagnostic}: {diagnostic.Cause}")));
    return log.Add(new CommandResult(result.ExitCode, RustLayerRun.Stdout(result), RustLayerRun.Stderr(result)));
  }

}
