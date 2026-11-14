using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Toolchains;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Formats.Llvm;

/// <summary>
/// The steps of <c>tests/features/formats/fmt-llvm-audit.feature</c>. The runs are real: the pinned
/// <c>pbc</c> and the LLVM tools of <c>tooling/toolchains.lock</c>, each required the way
/// <c>RequiresToolchain</c> requires it, so a scenario without them is inconclusive outside CI. A run
/// of <c>pbc</c> goes to the scenario's command log, where the shared steps check exit code and output.
/// </summary>
[Binding, Scope(Feature = "Audit of the PB-Compiler LLVM emitter")]
public sealed class PbEmitterAuditSteps(ScenarioWorkspace workspace, CommandLog log) {

  private CharacterizationEntry? _entry;
  private string? _program;
  private List<(string Name, string Text)> _modules = [];
  private List<string> _accesses = [];
  private string _source = "";
  private IReadOnlyList<string> _arms = [];
  private CharacterizationEntry? _edited;
  private IReadOnlyList<string> _problems = [];

  private static void Require(string key) => new RequiresToolchainAttribute(key).Check(Environment.GetEnvironmentVariable(ToolchainLocator.RequireVariable));

  private CommandResult Module => log.Last;

  [Given("^the pinned pb-compiler and a fixture whose characterization entry says outcome " + StepText.Quoted + "$")]
  public void PinnedCompilerAndFixture(string outcome) {
    Require("pb-compiler");
    this._entry = Characterization.Repository.Fixtures.First(entry => entry.Outcome == outcome && entry.LlvmAs == Characterization.Pass);
  }

  [When(@"^I run ""pbc --emit-llvm"" on that fixture$")]
  public void RunOnThatFixture() => log.Add(PbEmitterTools.Installed.EmitLlvm(PbCorpus.Resolve(this._entry!.Source)));

  [Then("^the output contains a line starting with " + StepText.Quoted + "$")]
  public void OutputHasALineStartingWith(string start)
    => Assert.That(this.Module.Stdout.Split('\n'), Has.Some.StartsWith(start), this.Module.Output);

  [Then("^llvm-as exits with 0 for that output$")]
  public void LlvmAsAccepts() {
    Require("llvm");
    var run = PbEmitterTools.Installed.Assemble(this.Module.Stdout);
    Assert.That(run.ExitCode, Is.EqualTo(0), $"{this._entry}: llvm-as: {run.Stderr}");
  }

  [Then(@"^""opt -passes=verify"" exits with 0 for that output$")]
  public void VerifierAccepts() {
    Require("llvm");
    var run = PbEmitterTools.Installed.Verify(this.Module.Stdout);
    Assert.That(run.ExitCode, Is.EqualTo(0), $"{this._entry}: opt -passes=verify: {run.Stderr}");
  }

  [Given("^the modules emitted for all fixtures whose characterization outcome is " + StepText.Quoted + "$")]
  public void ModulesOfAllFixtures(string outcome) {
    Require("pb-compiler");
    Assert.That(outcome, Is.EqualTo(Characterization.Emitted));
    this._modules = [.. PbEmitterCharacterizationTests.EmittedModules()];
  }

  [When("^their load and store instructions are listed$")]
  public void ListAccesses()
    => this._accesses = [.. this._modules.SelectMany(module => PbEmitterCharacterizationTests.MemoryAccesses(module.Text).Select(line => $"{module.Name}: {line}"))];

  [Then("^each of them ends with " + StepText.Quoted + "$")]
  public void EachEndsWith(string end) {
    Assert.That(this._accesses, Is.Not.Empty);
    Assert.That(this._accesses, Has.All.EndsWith(end));
  }

  [Given("^a PowerBASIC program " + StepText.Quoted + " with " + StepText.Quoted + "$")]
  public void Program(string main, string procedure) {
    Require("pb-compiler");
    this._program = workspace.Write("PROGRAM.BAS", main + "\n" + procedure + "\n");
  }

  [When(@"^I run ""pbc --emit-llvm"" on it$")]
  public void RunOnIt() => log.Add(PbEmitterTools.Installed.EmitLlvm(this._program!));

  [Then("^stdout contains no " + StepText.Quoted + "$")]
  public void StdoutContainsNo(string text) => Assert.That(log.Last.Stdout, Does.Not.Contain(text));

  [Given("^the pinned file " + StepText.Quoted + "$")]
  public void PinnedFile(string path) {
    Require("git");
    this._source = PbCorpus.ReadPinned(PbEmitterSource.Repository, path);
  }

  [When("^the IR instruction types named in its EmitInstruction switch are listed$")]
  public void ListArms() => this._arms = PbEmitterSource.InstructionArms(this._source);

  [Then("^each of them has a row in table (\\d) of " + StepText.Quoted + "$")]
  public void EachHasARow(int table, string document) {
    Assert.That(document, Is.EqualTo(PbEmitterAudit.RepositoryPath));
    var audit = PbEmitterAudit.Repository;
    Assert.That(this._arms, Is.Not.Empty);
    Assert.That(this._arms.Where(arm => !audit.Covers(table, arm)), Is.Empty);
  }

  [Given("^a characterization entry edited from " + StepText.Quoted + " to " + StepText.Quoted + "$")]
  public void EditedEntry(string from, string to) {
    Require("pb-compiler");
    Require("llvm");
    var entry = Characterization.Repository.Fixtures.First(candidate => candidate.Outcome == from);
    this._edited = entry with { Outcome = to, DeclinedBecause = "edited", LlvmAs = null, Verify = null, Sha256 = null };
  }

  [When("^the characterization tests run$")]
  public void CharacterizationRuns()
    => this._problems = Characterization.Compare(this._edited!, PbEmitterTools.Installed.Observe(PbCorpus.Resolve(this._edited!.Source)));

  [Then("^they fail naming that fixture and the recorded and actual outcome$")]
  public void TheyFail() {
    Assert.That(this._problems, Has.Count.EqualTo(1));
    Assert.That(this._problems[0], Does.StartWith($"{this._edited!.Name}: recorded outcome '{this._edited.Outcome}', actual '{Characterization.Emitted}'"));
  }

}
