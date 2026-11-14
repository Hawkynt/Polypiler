using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The steps of <c>tests/features/infra/infra-ci-jvm.feature</c>. Every source is compiled and
/// executed for real through <see cref="JvmCommands"/>, on the toolchains of
/// <c>tooling/toolchains.lock</c>; a scenario whose toolchains are missing is inconclusive, and fails
/// with <c>POLYPILER_REQUIRE_TOOLCHAINS=1</c>.
/// </summary>
[Binding, Scope(Feature = "Kotlin/Java CI layer")]
public sealed class JvmLayerSteps {

  private static readonly Dictionary<string, string> _compilerOfExtension = new(StringComparer.Ordinal) { [".java"] = "javac", [".kt"] = "kotlinc" };

  private readonly List<string> _sources = [];
  private readonly List<RunResult> _results = [];

  private RunResult Result => this._results.Count == 1 ? this._results[0] : throw new InvalidOperationException($"{this._results.Count} sources have run, not one");

  private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

  private static string Fixture(string path) {
    var name = Path.GetFileName(path);
    Assert.That(path, Is.EqualTo(JvmLayerTests.FixtureFolder + "/" + name), "the sources of the layer are its fixtures");
    return name;
  }

  private async Task CompileAndRun(string compiler) {
    foreach (var source in this._sources) {
      Assert.That(_compilerOfExtension[Path.GetExtension(source)], Is.EqualTo(compiler), $"{source} is not compiled with {compiler}");
      this._results.Add(await JvmLayerTests.Run(source));
    }
  }

  [Given("^the source " + StepText.Quoted + "$")]
  public void Source(string path) => this._sources.Add(Fixture(path));

  [Given("^the sources " + StepText.Quoted + " and " + StepText.Quoted + " printing " + StepText.Quoted + " and " + StepText.Quoted + @" \(Kotlin: " + StepText.Quoted + @"\)$")]
  public void SourcePair(string java, string kotlin, string sum, string shift, string kotlinShift) {
    var javaText = Encoding.UTF8.GetString(JvmLayerTests.Fixture(java));
    var kotlinText = Encoding.UTF8.GetString(JvmLayerTests.Fixture(kotlin));
    Assert.Multiple(() => {
      Assert.That(javaText, Does.Contain($"System.out.println({sum});").And.Contain($"System.out.println({shift});"));
      Assert.That(kotlinText, Does.Contain($"println({sum.Replace("Integer.", "Int.", StringComparison.Ordinal)})").And.Contain($"println({kotlinShift})"));
    });
    this._sources.AddRange([java, kotlin]);
  }

  [When("^it is compiled with " + StepText.Quoted + " and executed on the pinned JDK$")]
  [When("^it is compiled with " + StepText.Quoted + " and executed$")]
  [When("^it is compiled with " + StepText.Quoted + "$")]
  public Task Compile(string compiler) => this.CompileAndRun(compiler);

  [When("^both are compiled and executed$")]
  public async Task CompileBoth() {
    foreach (var source in this._sources)
      this._results.Add(await JvmLayerTests.Run(source));
  }

  [Then("^stdout is " + StepText.Quoted + "$")]
  public void StdoutIs(string expected) {
    Assert.That(this.Result.Outcome, Is.EqualTo(RunOutcome.Completed), () => string.Join('\n', this.Result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}\n{diagnostic.Cause}")));
    Assert.That(Text(this.Result.Stdout), Is.EqualTo(StepText.Unescape(expected) + "\n"));
  }

  [Then(@"^the exit code is (-?\d+)$")]
  public void ExitCode(int expected) => Assert.That(this.Result.ExitCode, Is.EqualTo(expected), Text(this.Result.Stderr));

  [Then("^each prints " + StepText.Quoted + " followed by " + StepText.Quoted + "$")]
  public void EachPrints(string first, string second) {
    Assert.That(this._results, Has.Count.EqualTo(2));
    Assert.That(this._results.Select(result => (result.Outcome, result.ExitCode, Text(result.Stdout))), Has.All.EqualTo((RunOutcome.Completed, 0, $"{first}\n{second}\n")));
  }

  [Then("^the two outputs are byte-identical$")]
  public void ByteIdentical() => Assert.That(JvmCommands.Difference(this._results[0].Stdout.Span, this._results[1].Stdout.Span), Is.Null);

  [Then("^the run outcome is " + StepText.Quoted + " with diagnostic " + StepText.Quoted + "$")]
  public void OutcomeWithDiagnostic(string outcome, string code) {
    Assert.That(this.Result.Outcome, Is.EqualTo(Enum.Parse<RunOutcome>(outcome)));
    Assert.That(this.Result.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { code }));
  }

  [Then("^the compiler output contains " + StepText.Quoted + "$")]
  public void CompilerOutputContains(string text) => Assert.That(this.Result.Diagnostics[0].Cause, Does.Contain(StepText.Unescape(text)).IgnoreCase);

  [Then("^the run outcome is " + StepText.Quoted + @" with exit code (-?\d+)$")]
  public void OutcomeWithExitCode(string outcome, int exitCode) {
    Assert.That(this.Result.Outcome, Is.EqualTo(Enum.Parse<RunOutcome>(outcome)));
    Assert.That(this.Result.ExitCode, Is.EqualTo(exitCode));
    Assert.That(this.Result.Diagnostics, Is.Empty);
  }

  [Then("^stderr contains " + StepText.Quoted + "$")]
  public void StderrContains(string text) => Assert.That(Text(this.Result.Stderr), Does.Contain(StepText.Unescape(text)));

}
