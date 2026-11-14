using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Toolchains;
using Reqnroll;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The steps of <c>tests/features/infra/infra-ci-native.feature</c>. Every build is real: the source is
/// copied into a run of the cross-language runner, compiled there by the pinned compiler with
/// <see cref="NativeFlags"/>, and the program it wrote is executed as the run's execute step. The run
/// goes to the scenario's command log, where the shared steps check exit code and output. A compiler
/// not used on this platform leaves the scenario inconclusive, as <c>RequiresToolchain</c> does, and
/// so does a sanitizer negative where the platform has no sanitizers: it is not run there, and never
/// counts as caught.
/// </summary>
[Binding, Scope(Feature = "Native CI layer")]
public sealed class NativeLayerSteps(CommandLog log) {

  /// <summary>Wraps a sandbox and records the commands it is asked to run.</summary>
  private sealed class RecordingSandbox(ISandbox inner) : ISandbox {

    public List<SandboxCommand> Commands { get; } = [];

    public IsolationLevel Isolation => inner.Isolation;

    public IReadOnlyList<string> CommandLine(SandboxCommand command) {
      this.Commands.Add(command);
      return inner.CommandLine(command);
    }

    public IProcessTreeMonitor Watch(Process process, RunLimits limits) => inner.Watch(process, limits);

  }

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private readonly RecordingSandbox _sandbox = new(Host);
  private string _source = "";
  private bool _needsSanitizers;
  private string? _lock;
  private RunResult? _result;

  private RunResult Result => this._result ?? throw new InvalidOperationException("nothing was compiled");

  private static void Require(string key) => new RequiresToolchainAttribute(key).Check(Environment.GetEnvironmentVariable(ToolchainLocator.RequireVariable));

  [Given("^the source " + StepText.Quoted + "$")]
  public void Source(string path) {
    this._source = path;
    Assert.That(File.Exists(Path.Combine(_root, path)), Is.True, path);
  }

  /// <summary>A source whose error only a sanitizer catches; the words after "that" say which.</summary>
  [Given("^the source " + StepText.Quoted + " that (?:reads|computes) .+$")]
  public void SanitizerNegative(string path) {
    this.Source(path);
    this._needsSanitizers = true;
  }

  [When("^it is compiled with " + StepText.Quoted + " using the sanitized flags and executed$")]
  public Task CompiledAndExecuted(string compiler) => this.CompileAsync(compiler, []);

  [When("^it is compiled with " + StepText.Quoted + " using the sanitized flags and " + StepText.Quoted + " and executed$")]
  public Task CompiledWithAndExecuted(string compiler, string define) => this.CompileAsync(compiler, [define]);

  [When("^it is compiled with " + StepText.Quoted + " using the sanitized flags$")]
  public Task Compiled(string compiler) => this.CompileAsync(compiler, []);

  [When("^it is compiled with " + StepText.Quoted + " using the sanitized flags and " + StepText.Quoted + "$")]
  public Task CompiledWith(string compiler, string define) => this.CompileAsync(compiler, [define]);

  /// <summary>
  /// Compiles the source in a run and executes the program it wrote. Whether the build fails or not,
  /// the run is the same; a scenario that only compiles checks that the build failed and nothing ran.
  /// </summary>
  private async Task CompileAsync(string compiler, IReadOnlyList<string> defines) {
    Require(compiler);
    if (this._needsSanitizers && NativeFlags.SanitizersOf(compiler, ToolchainLocator.CurrentPlatform) is null)
      Assert.Inconclusive($"sanitizers: unavailable for {ToolchainLocator.CurrentPlatform}/{compiler} ({NativeFlags.SanitizerTable}); this negative is not run here");

    var name = Path.GetFileName(this._source);
    var language = Path.GetExtension(name) == ".cpp" ? NativeLanguage.Cpp : NativeLanguage.C;
    var program = NativeFlags.Program("program");
    var files = new Dictionary<string, ReadOnlyMemory<byte>> { [name] = File.ReadAllBytes(Path.Combine(_root, this._source)) };
    var build = NativeFlags.Compile(compiler, language, name, program, defines);
    var plan = new RunPlan(files, [build], ProcessSpec.Built(program), RunLimits.Default, TrustLevel.Trusted);
    this._result = await new CrossLanguageRunner(this._sandbox, Toolchains).RunAsync(plan);

    var stdout = Encoding.UTF8.GetString(this.Result.Stdout.Span);
    var stderr = Encoding.UTF8.GetString(this.Result.Stderr.Span);
    var diagnostics = string.Join("\n", this.Result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}\n{diagnostic.Cause}"));
    TestContext.Out.WriteLine($"{ToolchainLocator.CurrentPlatform}/{compiler} ({this._sandbox.Isolation}): {build}");
    TestContext.Out.WriteLine($"outcome {this.Result.Outcome}, exit code {this.Result.ExitCode}\n--- stdout\n{stdout}--- stderr\n{stderr}--- diagnostics\n{diagnostics}");
    log.Add(new CommandResult(this.Result.ExitCode, stdout, stderr));
  }

  [Then("^the exit code is not 0$")]
  public void ExitCodeIsNotZero() {
    Assert.That(this.Result.Outcome, Is.EqualTo(RunOutcome.Completed), "the program was built and ran to its end");
    Assert.That(this.Result.ExitCode, Is.Not.Zero, log.Last.Output);
  }

  [Then("^the run outcome is " + StepText.Quoted + " with diagnostic " + StepText.Quoted + "$")]
  public void OutcomeWithDiagnostic(string outcome, string code) {
    Assert.That(this.Result.Outcome, Is.EqualTo(Enum.Parse<RunOutcome>(outcome)));
    var diagnostic = this.Result.Diagnostics.Single();
    Assert.That((diagnostic.Code, diagnostic.Severity), Is.EqualTo((code, DiagnosticSeverity.Error)), diagnostic.Cause);
  }

  [Then("^nothing is executed$")]
  public void NothingIsExecuted() {
    Assert.That(this._sandbox.Commands, Has.Count.EqualTo(1), "only the build step started");
    Assert.That(this.Result.Stdout.IsEmpty, Is.True);
  }

  [Given("^the lock fixture " + StepText.Quoted + "(?: pinning clang 0\\.0\\.1)?$")]
  public void LockFixture(string path) {
    this._lock = path;
    Assert.That(File.Exists(Path.Combine(_root, path)), Is.True, path);
    if (path.EndsWith("other-clang.lock", StringComparison.Ordinal))
      Require("clang");
  }

  /// <summary>Runs the probe step of the job, <c>polypiler-tooling toolchains probe</c>, against the lock fixture.</summary>
  [When("^" + StepText.Quoted + " is probed against it$")]
  public void ProbedAgainstIt(string key) => log.Add(Toolchain.Tooling(_root, "toolchains", "probe", "--lock", this._lock!, "--only", key));

}
