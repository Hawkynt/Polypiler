using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Verification.Running.Support;
using Polypiler.Tooling.Toolchains;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The steps of <c>tests/features/infra/infra-ci-basic.feature</c>. The runs are real: the pinned
/// <c>pbc</c> and DOSBox-X of <c>tooling/toolchains.lock</c>, each required the way
/// <c>RequiresToolchain</c> requires it, so a scenario is inconclusive without them outside CI and
/// fails without them in the job <c>layer-basic</c>. Every run is recorded under <c>artifacts/basic</c>.
/// </summary>
[Binding, Scope(Feature = "BASIC/PB CI layer")]
public sealed class BasicLayerSteps {

  private string _name = "";
  private byte[] _source = [];
  private RunLimits _limits = RunLimits.Default;
  private RunResult? _result;
  private string _table = "";

  private RunResult Result => this._result ?? throw new InvalidOperationException("nothing was run");

  private string Captured => PbCommands.Captured(this.Result) ?? throw new AssertionException($"the run wrote no {PbCommands.Output}: {this.Result.Outcome}, {string.Join("; ", this.Result.Diagnostics.Select(diagnostic => diagnostic.Message))}");

  private static void Require(string key) => new RequiresToolchainAttribute(key).Check(Environment.GetEnvironmentVariable(ToolchainLocator.RequireVariable));

  [Given("^the source " + StepText.Quoted + " of PB-Compiler at its pinned commit$")]
  public void UpstreamSource(string path) {
    this._name = Path.GetFileNameWithoutExtension(path);
    this._source = BasicUpstream.Read(path);
  }

  [Given("^the source " + StepText.Quoted + "$")]
  public void Source(string path) {
    this._name = Path.GetFileNameWithoutExtension(path);
    this._source = File.ReadAllBytes(Path.Combine(PrototypeFixture.RepositoryRoot, path));
  }

  [Given("^the source " + StepText.Quoted + " printing " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void SourcePrinting(string path, string first, string second) {
    this.Source(path);
    Assert.That(Encoding.ASCII.GetString(this._source).Split('\n').Select(line => line.TrimEnd('\r')), Is.SupersetOf(new[] { $"PRINT {first}", $"PRINT {second}" }));
  }

  [Given("^the source " + StepText.Quoted + @" and a wall-time limit of (\d+) seconds$")]
  public void SourceWithLimit(string path, int seconds) {
    this.Source(path);
    this._limits = RunnerTesting.Limits(wallTime: TimeSpan.FromSeconds(seconds));
  }

  [When(@"^it is compiled with ""pb-compiler"" and executed in ""dosbox-x""$")]
  public async Task CompiledAndExecuted() {
    Require(PbCommands.Compiler);
    Require(PbCommands.Emulator);
    await this.Run();
  }

  /// <summary>Runs the whole plan: the runner resolves the emulator before it starts the compiler, so both are required.</summary>
  [When(@"^it is compiled with ""pb-compiler""$")]
  public async Task Compiled() {
    Require(PbCommands.Compiler);
    Require(PbCommands.Emulator);
    await this.Run();
  }

  [Then("^the captured output equals PB-Compiler's " + StepText.Quoted + "$")]
  public void OutputEqualsUpstream(string path) {
    var expected = Encoding.Latin1.GetString(BasicUpstream.Read(path));
    Assert.That(BasicUpstream.Differences(this.Captured, expected), Is.Empty, this.Captured);
  }

  [Then("^its first three lines are " + StepText.Quoted + ", " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void FirstThreeLines(string first, string second, string third)
    => Assert.That(BasicUpstream.Trimmed(this.Captured).Take(3), Is.EqualTo(new[] { first, second, third }), this.Captured);

  [Then("^the captured output is " + StepText.Quoted + " followed by " + StepText.Quoted + "$")]
  public void OutputIs(string first, string second)
    => Assert.That(this.Captured, Is.EqualTo($"{first} \n{second} \n"), "PowerBASIC prints a number with a blank after it");

  [Then("^the run outcome is " + StepText.Quoted + " with diagnostic " + StepText.Quoted + "$")]
  public void OutcomeIs(string outcome, string code)
    => Assert.That((this.Result.Outcome.ToString(), this.Result.Diagnostics.Select(diagnostic => diagnostic.Code)), Is.EqualTo((outcome, new[] { code })));

  [Then("^no executable is started in the emulator$")]
  public void NoEmulatorRun()
    => Assert.That((this.Result.Collected.Keys, Encoding.UTF8.GetString(this.Result.Stdout.Span)), Is.EqualTo((Array.Empty<string>(), "")));

  [Then("^no emulator process is left running$")]
  public void NoEmulatorLeft()
    => Assert.That(RunnerTesting.Survivors(this.Result.WorkingDirectory!), Is.Empty);

  [When("^the layer writes its oracle availability table$")]
  public void WritesTable() {
    this._table = BasicOracleTable.Render(BasicOracleTable.Load(PrototypeFixture.RepositoryRoot));
    var folder = Path.Combine(PrototypeFixture.RepositoryRoot, "artifacts", "basic");
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "oracle-availability.md"), this._table);
    var expected = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, BasicOracleTable.ExpectedPath));
    Assert.That(BasicOracleTable.Differences(this._table, expected), Is.Empty);
  }

  [Then("^the row for " + StepText.Quoted + " reads " + StepText.Quoted + "$")]
  public void RowReads(string oracle, string availability)
    => Assert.That(this._table.Split('\n'), Has.One.StartsWith($"| {oracle} |").And.EndsWith($"| {availability} |"));

  [Then("^the rows for " + StepText.Quoted + " and " + StepText.Quoted + " read " + StepText.Quoted + "$")]
  public void RowsRead(string first, string second, string availability) {
    this.RowReads(first, availability);
    this.RowReads(second, availability);
  }

  private async Task Run() {
    this._result = await PbCommands.RunAsync(PbCommands.Plan(this._source, this._limits));
    PbCommands.Record(this._name, this._result);
  }

}
