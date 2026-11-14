using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Running;
using Polypiler.Tests.Verification.Running.Support;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Verification.Running;

/// <summary>
/// The runner with real processes: the probe and the fixture projects, run in every sandbox this host
/// offers. Each case checks that the run left no process and no directory behind.
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public sealed class CrossLanguageRunnerTests {

  /// <summary>Wraps a sandbox, recording every command it is asked to run; it can fail to watch, as a Job Object can.</summary>
  private sealed class RecordingSandbox(ISandbox inner, bool failToWatch = false) : ISandbox {

    public List<SandboxCommand> Commands { get; } = [];

    public IsolationLevel Isolation => inner.Isolation;

    public IReadOnlyList<string> CommandLine(SandboxCommand command) {
      this.Commands.Add(command);
      return inner.CommandLine(command);
    }

    public IProcessTreeMonitor Watch(Process process, RunLimits limits)
      => failToWatch ? throw new System.ComponentModel.Win32Exception(5, "the job refused the process") : inner.Watch(process, limits);

  }

  /// <summary>A resolver that hands out one path for every key.</summary>
  private sealed class FixedResolver(string executable) : IToolchainResolver {

    public ToolchainResolution Resolve(string toolchain) => ToolchainResolution.Found(executable);

  }

  /// <summary>Every sandbox of this host: the one the factory chooses, and on Linux both polling tables and bubblewrap when it works.</summary>
  private static IEnumerable<TestCaseData> Sandboxes() {
    yield return new TestCaseData("host").SetArgDisplayNames("host");
    if (OperatingSystem.IsLinux()) {
      yield return new TestCaseData("proc").SetArgDisplayNames("polling /proc");
      yield return new TestCaseData("ps").SetArgDisplayNames("polling ps");
      yield return new TestCaseData("bubblewrap").SetArgDisplayNames("bubblewrap");
    }
  }

  private static ISandbox Sandbox(string name) => name switch {
    "proc" => Polling(new ProcFileSystemTable()),
    "ps" => Polling(new PsCommandTable()),
    "bubblewrap" => Bubblewrap ?? Inconclusive(),
    _ => Host
  };

  private static ISandbox Inconclusive() {
    Assert.Inconclusive("bubblewrap is not installed or cannot unshare namespaces on this host; the host case covers the polling sandbox");
    return null!;
  }

  /// <summary>Runs a plan and checks what every run must leave: no process and no directory.</summary>
  private static async Task<RunResult> Run(ISandbox sandbox, RunPlan plan, IToolchainResolver? toolchains = null) {
    var recording = new RecordingSandbox(sandbox);
    var result = await new CrossLanguageRunner(recording, toolchains ?? Toolchains).RunAsync(plan);
    if (result.WorkingDirectory is { } directory) {
      Assert.That(Directory.Exists(directory), Is.False, "the working directory is removed");
      Assert.That(Survivors(directory), Is.Empty, "no process of the run is alive");
      Assert.That(recording.Commands.Select(command => command.WorkingDirectory).Distinct(), Is.EqualTo(new[] { directory }));
    }

    Assert.That(result.Isolation, Is.EqualTo(sandbox.Isolation));
    return result;
  }

  private static void AssertViolation(RunResult result, RunOutcome outcome, string code, string message) {
    Assert.That(result.Outcome, Is.EqualTo(outcome), Encoding.UTF8.GetString(result.Stderr.Span));
    Assert.That(result.ExitCode, Is.EqualTo(-1));
    var diagnostic = result.Diagnostics.Single();
    Assert.That((diagnostic.Code, diagnostic.Severity, diagnostic.Message), Is.EqualTo((code, DiagnosticSeverity.Error, message)));
  }

  /// <summary>Test design 2 and 5: no build step, standard input passed, bytes preserved.</summary>
  [TestCaseSource(nameof(Sandboxes))]
  public async Task Echo_PassesStandardInputThroughByteForByte(string name) {
    var result = await Run(Sandbox(name), ProbePlan(Probe(new byte[] { 0x01, 0x02, 0xFF, 0x0D, 0x0A }, "echo")));
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Completed), Encoding.UTF8.GetString(result.Stderr.Span));
    Assert.That((result.ExitCode, result.Stdout.ToArray(), result.Stderr.Length, result.Diagnostics.Count), Is.EqualTo((0, new byte[] { 0x01, 0x02, 0xFF, 0x0D, 0x0A }, 0, 0)));
    Assert.That(result.WallTime, Is.GreaterThan(TimeSpan.Zero));
  }

  /// <summary>Test design 3: the executed program's own failure is its exit code, not a diagnostic.</summary>
  [TestCaseSource(nameof(Sandboxes))]
  public async Task ANonZeroExit_IsCompletedWithThatExitCode(string name) {
    var result = await Run(Sandbox(name), ProbePlan(Probe("exit", "3")));
    Assert.That((result.Outcome, result.ExitCode, result.Diagnostics.Count), Is.EqualTo((RunOutcome.Completed, 3, 0)));
  }

  /// <summary>Test design 10: no build step and no file.</summary>
  [TestCaseSource(nameof(Sandboxes))]
  public async Task AnEmptyPlan_Runs(string name) {
    var result = await Run(Sandbox(name), new RunPlan(new Dictionary<string, ReadOnlyMemory<byte>>(), [], new ProcessSpec(DotNet, "--version"), RunLimits.Default, TrustLevel.Trusted));
    Assert.That((result.Outcome, result.ExitCode), Is.EqualTo((RunOutcome.Completed, 0)));
    Assert.That(Encoding.UTF8.GetString(result.Stdout.Span), Does.Match(@"^\d+\.\d+\.\d+"));
  }

  /// <summary>Test design 9 and 15: a step just inside the time limit completes; one beyond it is killed.</summary>
  [TestCaseSource(nameof(Sandboxes))]
  public async Task TheTimeLimit_KillsAStepThatRunsTooLong(string name) {
    var sandbox = Sandbox(name);
    var inside = await Run(sandbox, ProbePlan(Probe("sleep", "100"), Limits(wallTime: TimeSpan.FromSeconds(10))));
    Assert.That(inside.Outcome, Is.EqualTo(RunOutcome.Completed));
    var beyond = await Run(sandbox, ProbePlan(Probe("sleep", "5000"), Limits(wallTime: TimeSpan.FromMilliseconds(500))));
    AssertViolation(beyond, RunOutcome.TimedOut, "PLY0031", "time limit of 500 ms exceeded");
    Assert.That(beyond.WallTime, Is.LessThan(TimeSpan.FromSeconds(4)));
    Assert.That(beyond.Diagnostics[0].ExitCode, Is.EqualTo(ExitCode.BudgetExceeded));
  }

  /// <summary>Test design 16.</summary>
  [TestCaseSource(nameof(Sandboxes))]
  public async Task TheMemoryLimit_KillsATreeThatHoldsTooMuch(string name) {
    var result = await Run(Sandbox(name), ProbePlan(Probe("alloc", "512"), Limits(memoryBytes: 128L << 20)));
    AssertViolation(result, RunOutcome.MemoryExceeded, "PLY0032", "memory limit of 134217728 bytes exceeded");
    Assert.That(result.PeakMemoryBytes, Is.GreaterThan(128L << 20));
  }

  [Test]
  public async Task AStepWithinItsMemory_ReportsWhatItUsed() {
    var result = await Run(Host, ProbePlan(Probe("alloc", "64")));
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Completed));
    Assert.That(result.PeakMemoryBytes, Is.GreaterThan(64L << 20).And.LessThan(1L << 30));
    Assert.That(result.CpuTime, Is.GreaterThan(TimeSpan.Zero));
  }

  /// <summary>Test design 8: output at the limit is kept whole; one byte more ends the step, and what was captured stops at the limit.</summary>
  [TestCaseSource(nameof(Sandboxes))]
  public async Task TheOutputLimit_CutsTheCaptureAndKillsTheStep(string name) {
    var sandbox = Sandbox(name);
    var limits = Limits(outputBytes: 1048576);
    var at = await Run(sandbox, ProbePlan(Probe("spew", "1048576"), limits));
    Assert.That((at.Outcome, at.Stdout.Length), Is.EqualTo((RunOutcome.Completed, 1048576)));
    var over = await Run(sandbox, ProbePlan(Probe("spew", "1048577"), limits));
    AssertViolation(over, RunOutcome.OutputExceeded, "PLY0033", "output limit of 1048576 bytes exceeded");
    Assert.That(over.Stdout.Length, Is.EqualTo(1048576));
    var far = await Run(sandbox, ProbePlan(Probe("spew", "2097152"), limits));
    Assert.That((far.Outcome, far.Stdout.Length), Is.EqualTo((RunOutcome.OutputExceeded, 1048576)));
    Assert.That(far.Stdout.ToArray().All(value => value == 'x'), Is.True);
  }

  /// <summary>A program that ends at once may end before the watch sees its output: the output still counts.</summary>
  [Test]
  public async Task OutputBeyondTheLimitOfAProgramThatHasEnded_IsStillExceeded() {
    var result = await Run(Host, ProbePlan(Probe("spew", "10"), Limits(outputBytes: 5)));
    AssertViolation(result, RunOutcome.OutputExceeded, "PLY0033", "output limit of 5 bytes exceeded");
    Assert.That(result.Stdout.ToArray(), Is.EqualTo("xxxxx"u8.ToArray()));
  }

  [Test]
  public async Task StandardInputTheProgramDoesNotRead_IsNoError() {
    var result = await Run(Host, ProbePlan(Probe(new byte[8 << 20], "exit", "0")));
    Assert.That((result.Outcome, result.ExitCode), Is.EqualTo((RunOutcome.Completed, 0)));
  }

  /// <summary>Test design 7: standard error counts too.</summary>
  [Test]
  public async Task StandardError_CountsAgainstTheOutputLimit() {
    var result = await Run(Host, ProbePlan(Probe("spew", "4096", "stderr"), Limits(outputBytes: 1000)));
    AssertViolation(result, RunOutcome.OutputExceeded, "PLY0033", "output limit of 1000 bytes exceeded");
    Assert.That((result.Stdout.Length, result.Stderr.Length), Is.EqualTo((0, 1000)));
  }

  /// <summary>Test design 11: within the process limit nothing is touched; beyond it the children fail and the probe says so.</summary>
  [TestCaseSource(nameof(Sandboxes))]
  public async Task TheProcessLimit_StopsATreeFromGrowing(string name) {
    var sandbox = Sandbox(name);
    var within = await Run(sandbox, ProbePlan(Probe("spawn", "4"), Limits(processes: 8)));
    Assert.That((within.Outcome, within.ExitCode), Is.EqualTo((RunOutcome.Completed, 0)), Encoding.UTF8.GetString(within.Stderr.Span));
    var beyond = await Run(sandbox, ProbePlan(Probe("spawn", "16"), Limits(processes: 8)));
    Assert.That((beyond.Outcome, beyond.ExitCode), Is.EqualTo((RunOutcome.Completed, 1)));
  }

  /// <summary>Test design 17: a child that outlives its parent is killed with the run.</summary>
  [TestCaseSource(nameof(Sandboxes))]
  public async Task AChildThatOutlivesItsParent_IsKilledWithTheRun(string name) {
    var result = await Run(Sandbox(name), ProbePlan(Probe("spawn", "1", "detach")));
    Assert.That((result.Outcome, result.ExitCode), Is.EqualTo((RunOutcome.Completed, 0)));
    Assert.That(result.WallTime, Is.LessThan(TimeSpan.FromSeconds(30)), "the run does not wait for the child's 60 s");
  }

  /// <summary>A sandbox whose monitor sees no process and kills none, as if every child slipped out of the tree.</summary>
  private sealed class BlindSandbox : ISandbox, IProcessTreeMonitor {

    public IsolationLevel Isolation => IsolationLevel.Limited;

    public IReadOnlyList<string> CommandLine(SandboxCommand command) => [command.Executable, .. command.Arguments];

    public IProcessTreeMonitor Watch(Process process, RunLimits limits) => this;

    public ResourceSample Sample() => default;

    public void KillAll() { }

    public void Dispose() { }

  }

  /// <summary>A process that escaped the tree keeps the pipes open; the run waits the drain time for it, and no longer.</summary>
  [Test]
  public async Task AProcessThatEscapedTheTree_DelaysTheRunByTheDrainTimeOnly() {
    if (!OperatingSystem.IsLinux()) {
      Assert.Pass("the escaped process is found and killed by its working directory, which only Linux shows");
      return;
    }

    var recording = new RecordingSandbox(new BlindSandbox());
    var result = await new CrossLanguageRunner(recording, Toolchains).RunAsync(ProbePlan(Probe("spawn", "1", "detach")));
    var escaped = Survivors(recording.Commands.Single().WorkingDirectory);
    foreach (var pid in escaped) {
      using var process = Process.GetProcessById(pid);
      process.Kill();
    }

    Assert.That((result.Outcome, result.ExitCode), Is.EqualTo((RunOutcome.Completed, 0)));
    Assert.That(result.WallTime, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(10)).And.LessThan(TimeSpan.FromSeconds(40)));
    Assert.That(escaped, Has.Count.EqualTo(1), "the child the monitor never saw");
  }

  /// <summary>Test design 13 and 14: the second of two build steps fails; nothing is executed and the cause is its standard error.</summary>
  [Test]
  public async Task AFailingBuildStep_EndsTheRunBeforeTheExecuteStep() {
    var recording = new RecordingSandbox(Host);
    var plan = ProbePlan(Probe("echo"), null, TrustLevel.Trusted, Probe("write", "built.txt"), Probe("no-such-verb"));
    var result = await new CrossLanguageRunner(recording, Toolchains).RunAsync(plan);
    Assert.That((result.Outcome, result.ExitCode, result.Stdout.Length, result.Stderr.Length), Is.EqualTo((RunOutcome.BuildFailed, -1, 0, 0)));
    var diagnostic = result.Diagnostics.Single();
    Assert.That((diagnostic.Code, diagnostic.Message, diagnostic.ExitCode), Is.EqualTo(("PLY0030", "target build failed: 'dotnet-sdk polypiler-probe.dll no-such-verb' exited with 2", ExitCode.TargetBuildFailure)));
    Assert.That(diagnostic.Cause, Does.StartWith("usage: polypiler-probe"));
    Assert.That(recording.Commands, Has.Count.EqualTo(2), "the execute step never started");
    Assert.That(Directory.Exists(result.WorkingDirectory), Is.False, "the first step's output is gone with the directory");
  }

  [Test]
  public async Task ABuildStepThatPrintsNothing_SaysSo() {
    var result = await Run(Host, ProbePlan(Probe("echo"), null, TrustLevel.Trusted, Probe("exit", "4")));
    Assert.That(result.Diagnostics.Single().Cause, Is.EqualTo("the step printed nothing"));
  }

  [Test]
  public async Task ABuildStepBeyondALimit_EndsTheRunWithThatLimit() {
    var result = await Run(Host, ProbePlan(Probe("echo"), Limits(wallTime: TimeSpan.FromMilliseconds(500)), TrustLevel.Trusted, Probe("sleep", "5000")));
    AssertViolation(result, RunOutcome.TimedOut, "PLY0031", "time limit of 500 ms exceeded");
  }

  /// <summary>Test design 1: build, execute, compare.</summary>
  [Test]
  public async Task TheHelloProject_IsBuiltExecutedAndCompared() {
    var plan = new RunPlan(FixtureFiles("hello"), [new ProcessSpec(DotNet, "build", "-c", "Release", "-o", "out")], new ProcessSpec(DotNet, "out/hello.dll"), RunLimits.Default, TrustLevel.Trusted);
    var result = await Run(Host, plan);
    Assert.That((result.Outcome, result.ExitCode), Is.EqualTo((RunOutcome.Completed, 0)), string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Cause)));
    Assert.That(OutputComparison.Compare("hello 42\n"u8, result.Stdout.Span, ComparisonMode.Exact).IsEqual, Is.True);
  }

  /// <summary>Test design 13: the SDK writes its errors to standard output, which becomes the cause.</summary>
  [Test]
  public async Task TheBrokenProject_FailsToBuild() {
    var plan = new RunPlan(FixtureFiles("broken"), [new ProcessSpec(DotNet, "build", "-c", "Release", "-o", "out")], new ProcessSpec(DotNet, "out/broken.dll"), RunLimits.Default, TrustLevel.Trusted);
    var result = await Run(Host, plan);
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.BuildFailed));
    Assert.That(result.Diagnostics.Single().Cause, Does.Contain("CS1002"));
  }

  /// <summary>Test design 19: no process starts and no directory is made.</summary>
  [Test]
  public async Task AnUntrustedPlanOnALimitedHost_IsRefused() {
    var recording = new RecordingSandbox(new PollingSandbox(new ProcFileSystemTable()));
    var result = await new CrossLanguageRunner(recording, Toolchains).RunAsync(ProbePlan(Probe("echo"), trust: TrustLevel.Untrusted));
    Assert.That((result.Outcome, result.ExitCode, result.Isolation, result.WorkingDirectory), Is.EqualTo((RunOutcome.Refused, -1, IsolationLevel.Limited, (string?)null)));
    var diagnostic = result.Diagnostics.Single();
    Assert.That((diagnostic.Code, diagnostic.Message, diagnostic.ExitCode), Is.EqualTo(("PLY0034", "isolation is unavailable on this host; untrusted input was not executed", ExitCode.EnvironmentError)));
    Assert.That(recording.Commands, Is.Empty);
  }

  /// <summary>Test design 5 and 18: an isolated host runs untrusted code, without network and without the caller's home.</summary>
  [Test]
  public async Task AnIsolatedRun_HasNoNetworkAndCannotWriteOutsideItsDirectory() {
    var sandbox = Bubblewrap ?? Inconclusive();
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var accepted = 0;
    var accepting = Task.Run(async () => {
      try {
        while (true) {
          using var client = await listener.AcceptTcpClientAsync();
          Interlocked.Increment(ref accepted);
        }
      } catch (Exception exception) when (exception is SocketException or ObjectDisposedException) {
        // The listener was stopped.
      }
    });
    var port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    var outside = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $"polypiler-isolation-{Guid.NewGuid():N}.txt");

    var connect = await Run(sandbox, ProbePlan(Probe("connect", "127.0.0.1", port), trust: TrustLevel.Untrusted));
    var write = await Run(sandbox, ProbePlan(Probe("write", outside), trust: TrustLevel.Untrusted));
    var echo = await Run(sandbox, ProbePlan(Probe(new byte[] { 42 }, "echo"), trust: TrustLevel.Untrusted));
    listener.Stop();
    await accepting;

    Assert.That((connect.Outcome, connect.ExitCode), Is.EqualTo((RunOutcome.Completed, 1)));
    Assert.That((write.Outcome, write.ExitCode), Is.EqualTo((RunOutcome.Completed, 1)));
    Assert.That(echo.Stdout.ToArray(), Is.EqualTo(new byte[] { 42 }));
    Assert.That(accepted, Is.Zero);
    Assert.That(File.Exists(outside), Is.False);
  }

  /// <summary>Test design 20: a toolchain that is not installed refuses the plan before anything starts.</summary>
  [Test]
  public async Task AMissingToolchain_RefusesThePlan() {
    var recording = new RecordingSandbox(Host);
    var toolchains = Resolver(Path.Combine(Polypiler.Prototype.Testing.PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "toolchains", "ghost.lock"));
    var result = await new CrossLanguageRunner(recording, toolchains).RunAsync(ProbePlan(new ProcessSpec("ghost", "--version"), null, TrustLevel.Trusted, new ProcessSpec("ghost", "build")));
    Assert.That((result.Outcome, result.WorkingDirectory), Is.EqualTo((RunOutcome.Refused, (string?)null)));
    Assert.That(result.Diagnostics.Single().Message, Does.StartWith("toolchain 'ghost' is not installed (probe: no-such-binary-polypiler --version)"));
    Assert.That(recording.Commands, Is.Empty);
  }

  /// <summary>Test design 22: cancelling kills the tree and removes the directory before the exception arrives.</summary>
  [Test]
  public async Task Cancelling_KillsTheTreeAndRemovesTheDirectory() {
    var recording = new RecordingSandbox(Host);
    using var cancellation = new CancellationTokenSource();
    // Cancelled from a thread of its own: a timer would wait for the thread pool, which the parallel tests keep busy.
    new Thread(() => {
      Thread.Sleep(100);
      cancellation.Cancel();
    }).Start();
    var clock = Stopwatch.StartNew();
    Assert.That(async () => await new CrossLanguageRunner(recording, Toolchains).RunAsync(ProbePlan(Probe("sleep", "5000")), cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
    Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(4)));
    var directory = recording.Commands.Single().WorkingDirectory;
    Assert.That(Directory.Exists(directory), Is.False);
    Assert.That(Survivors(directory), Is.Empty);
  }

  [Test]
  public void ACancelledToken_StartsNothing() {
    var recording = new RecordingSandbox(Host);
    Assert.That(async () => await new CrossLanguageRunner(recording, Toolchains).RunAsync(ProbePlan(Probe("echo")), new CancellationToken(true)), Throws.InstanceOf<OperationCanceledException>());
    Assert.That(recording.Commands, Is.Empty);
    Assert.That(async () => await StepLauncher.RunAsync(recording, "x", Probe("echo"), Path.GetTempPath(), RunLimits.Default, new CancellationToken(true)), Throws.InstanceOf<OperationCanceledException>());
  }

  /// <summary>Started directly, a missing executable cannot start; bubblewrap and setsid would start and report it as their own exit code.</summary>
  [Test]
  public void AnExecutableThatCannotStart_IsAnExceptionAndLeavesNoDirectory() {
    var recording = new RecordingSandbox(OperatingSystem.IsWindows() ? Host : new PollingSandbox(OperatingSystem.IsLinux() ? new ProcFileSystemTable() : new PsCommandTable()));
    var runner = new CrossLanguageRunner(recording, new FixedResolver(Path.Combine(Path.GetTempPath(), "no-such-executable-polypiler")));
    Assert.That(async () => await runner.RunAsync(ProbePlan(Probe("echo"))), Throws.InstanceOf<System.ComponentModel.Win32Exception>());
    Assert.That(Directory.Exists(recording.Commands.Single().WorkingDirectory), Is.False);
  }

  [Test]
  public void ASandboxThatCannotWatch_KillsTheProcessItStarted() {
    var recording = new RecordingSandbox(Host, failToWatch: true);
    Assert.That(async () => await new CrossLanguageRunner(recording, Toolchains).RunAsync(ProbePlan(Probe("sleep", "60000"))), Throws.InstanceOf<System.ComponentModel.Win32Exception>());
    var directory = recording.Commands.Single().WorkingDirectory;
    Assert.That(Directory.Exists(directory), Is.False);
    Assert.That(Survivors(directory), Is.Empty);
  }

  [Test]
  public void ANullPlan_IsRefused()
    => Assert.That(async () => await new CrossLanguageRunner(Host, Toolchains).RunAsync(null!), Throws.ArgumentNullException);

}
