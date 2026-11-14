using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/infra-runner.feature</c>. Every run is a real one: the fixture
/// projects are built by the .NET SDK and the probe program runs in the sandbox of this host. A command
/// of the feature names its toolchain by the executable, <c>dotnet</c> for the key <c>dotnet-sdk</c>.
/// </summary>
[Binding, Scope(Feature = "Sandboxed cross-language runner")]
public sealed class RunnerSteps {

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

  private static readonly Dictionary<string, string> _toolchainOfCommand = new(StringComparer.Ordinal) { ["dotnet"] = DotNet };

  private IReadOnlyDictionary<string, ReadOnlyMemory<byte>> _files = new Dictionary<string, ReadOnlyMemory<byte>>();
  private readonly List<ProcessSpec> _build = [];
  private ProcessSpec? _execute;
  private RunLimits _limits = RunLimits.Default;
  private RecordingSandbox _sandbox = new(Host);
  private RunResult? _result;
  private readonly List<RunResult> _probeRuns = [];
  private TcpListener? _listener;
  private Task? _accepting;
  private int _accepted;
  private string? _outside;
  private byte[] _expected = [];
  private byte[] _actual = [];
  private ComparisonResult? _comparison;

  private RunResult Result => this._result ?? throw new InvalidOperationException("no plan has run");

  private static ProcessSpec Command(string text) {
    var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return new(_toolchainOfCommand[words[0]], words[1..]);
  }

  private async Task<RunResult> RunAsync(ProcessSpec execute, TrustLevel trust) {
    var plan = new RunPlan(this._files, this._build, execute, this._limits, trust);
    return await new CrossLanguageRunner(this._sandbox, Toolchains).RunAsync(plan);
  }

  /// <summary>Stops the listener a scenario opened.</summary>
  [AfterScenario]
  public void StopListener() => this._listener?.Dispose();

  [Given("^the fixture project " + StepText.Quoted + " and the build step " + StepText.Quoted + "$")]
  public void FixtureProject(string project, string build) {
    this._files = FixtureFiles(project);
    this._build.Add(Command(build));
    this._execute = Command($"dotnet out/{project}.dll");
  }

  [Given("^the execute step " + StepText.Quoted + "$")]
  public void ExecuteStep(string command) => this._execute = Command(command);

  [Given("^the probe invoked as " + StepText.Quoted + "$")]
  public void ProbeInvoked(string arguments) {
    this._files = ProbeFiles;
    this._execute = Probe(arguments.Split(' '));
  }

  [Given(@"^the limits wall time (\d+) ms, memory (\d+) MiB, output (\d+) bytes$")]
  public void LimitsAre(int wall, long memory, long output)
    => this._limits = Limits(TimeSpan.FromMilliseconds(wall), memory << 20, output);

  [Given("^the probe invoked as " + StepText.Quoted + @" with an output limit of (\d+) bytes$")]
  public void ProbeWithOutputLimit(string arguments, long output) {
    this.ProbeInvoked(arguments);
    this._limits = Limits(outputBytes: output);
  }

  [When("^the plan runs with default limits as trusted$")]
  public async Task RunsWithDefaultLimits() {
    this._limits = RunLimits.Default;
    await this.RunsAsTrusted();
  }

  [When("^the plan runs as trusted$")]
  public async Task RunsAsTrusted() => this._result = await this.RunAsync(this._execute!, TrustLevel.Trusted);

  [Then(@"^the outcome is (\w+) with exit code (-?\d+)$")]
  public void OutcomeWithExitCode(RunOutcome outcome, int exitCode)
    => Assert.That((this.Result.Outcome, this.Result.ExitCode), Is.EqualTo((outcome, exitCode)), string.Join("\n", this.Result.Diagnostics));

  [Then(@"^the outcome is (\w+)$")]
  public void OutcomeIs(RunOutcome outcome)
    => Assert.That(this.Result.Outcome, Is.EqualTo(outcome), string.Join("\n", this.Result.Diagnostics.Select(diagnostic => $"{diagnostic}: {diagnostic.Cause}")));

  [Then(@"^stdout is exactly the (\d+) bytes " + StepText.Quoted + "$")]
  public void StdoutIsExactly(int count, string text) {
    var expected = Encoding.UTF8.GetBytes(StepText.Unescape(text));
    Assert.That(expected, Has.Length.EqualTo(count));
    Assert.That(this.Result.Stdout.ToArray(), Is.EqualTo(expected));
  }

  [Then("^comparing stdout with the expected " + StepText.Quoted + @" in (\w+) mode yields Equal$")]
  public void ComparingYieldsEqual(string expected, ComparisonMode mode)
    => Assert.That(OutputComparison.Compare(Encoding.UTF8.GetBytes(StepText.Unescape(expected)), this.Result.Stdout.Span, mode), Is.SameAs(ComparisonResult.Equal));

  [Then("^the working directory no longer exists$")]
  public void WorkingDirectoryIsGone() {
    Assert.That(this.Result.WorkingDirectory, Is.Not.Null);
    Assert.That(Directory.Exists(this.Result.WorkingDirectory), Is.False);
  }

  [Then("^the diagnostics contain exactly one error with code " + StepText.Quoted + "$")]
  public void ExactlyOneError(string code) {
    var diagnostic = this.Result.Diagnostics.Single();
    Assert.That((diagnostic.Code, diagnostic.Severity), Is.EqualTo((code, DiagnosticSeverity.Error)));
  }

  [Then("^stdout of the execute step is empty$")]
  public void ExecuteStepStdoutIsEmpty() {
    Assert.That(this.Result.Stdout.IsEmpty, Is.True);
    Assert.That(this._sandbox.Commands, Has.Count.EqualTo(this._build.Count), "the execute step never started");
  }

  [Then("^the diagnostics contain an error with code " + StepText.Quoted + "$")]
  public void ContainsError(string code)
    => Assert.That(this.Result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.Code), Does.Contain(code));

  [Then("^no process of the run is alive afterwards$")]
  public void NoProcessAlive() {
    Assert.That(this.Result.ExitCode, Is.EqualTo(-1), "a killed step has no exit code of its own");
    Assert.That(Survivors(this.Result.WorkingDirectory!), Is.Empty);
    Assert.That(Directory.Exists(this.Result.WorkingDirectory), Is.False);
  }

  [Then(@"^the captured stdout has exactly (\d+) bytes$")]
  public void CapturedStdoutHas(int count) => Assert.That(this.Result.Stdout.Length, Is.EqualTo(count));

  /// <summary>
  /// The sandbox of the scenario. Only bubblewrap isolates; on a host without a working bubblewrap the
  /// isolated scenario runs on the host's own sandbox and checks what that host promises instead: an
  /// untrusted plan is refused, so nothing reaches the network or the home directory either.
  /// </summary>
  [Given(@"^a host with isolation level (Isolated|Limited)$")]
  public void HostWithIsolation(IsolationLevel isolation) {
    ISandbox sandbox = isolation == IsolationLevel.Limited
      ? OperatingSystem.IsWindows() ? Host : Polling(OperatingSystem.IsLinux() ? new ProcFileSystemTable() : new PsCommandTable())
      : (ISandbox?)Bubblewrap ?? Host;
    if (sandbox.Isolation != isolation)
      TestContext.Out.WriteLine($"This host offers no isolation ({Environment.OSVersion}): the untrusted runs below must be refused.");
    this._sandbox = new(sandbox);
  }

  [Given(@"^a TCP listener on 127\.0\.0\.1 opened by the test on a free port$")]
  public void OpenListener() {
    this._listener = new TcpListener(IPAddress.Loopback, 0);
    this._listener.Start();
    var listener = this._listener;
    this._accepting = Task.Run(async () => {
      try {
        while (true) {
          using var client = await listener.AcceptTcpClientAsync();
          Interlocked.Increment(ref this._accepted);
        }
      } catch (Exception exception) when (exception is SocketException or ObjectDisposedException or InvalidOperationException) {
        // The listener was stopped, possibly before the first accept: two refused runs end at once.
      }
    });
  }

  [When("^the probe runs " + StepText.Quoted + " with that port and then " + StepText.Quoted + " with a path in the caller's home directory$")]
  public async Task ProbeRunsConnectAndWrite(string connect, string write) {
    this._files = ProbeFiles;
    var port = ((IPEndPoint)this._listener!.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
    this._outside = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $"polypiler-isolation-{Guid.NewGuid():N}.txt");
    this._probeRuns.Add(await this.RunAsync(Probe([.. connect.Split(' '), port]), TrustLevel.Untrusted));
    this._probeRuns.Add(await this.RunAsync(Probe([.. write.Split(' '), this._outside]), TrustLevel.Untrusted));
    this._listener.Stop();
    await this._accepting!;
  }

  [Then(@"^both probe runs exit with code (\d+)$")]
  public void BothProbeRunsExitWith(int exitCode) {
    if (this._sandbox.Isolation == IsolationLevel.Isolated) {
      Assert.That(this._probeRuns.Select(run => (run.Outcome, run.ExitCode)), Is.EqualTo(new[] { (RunOutcome.Completed, exitCode), (RunOutcome.Completed, exitCode) }));
      return;
    }

    Assert.That(this._probeRuns.Select(run => (run.Outcome, run.Diagnostics.Single().Code)), Is.EqualTo(new[] { (RunOutcome.Refused, "PLY0034"), (RunOutcome.Refused, "PLY0034") }));
  }

  [Then(@"^the listener has accepted (\d+) connections$")]
  public void ListenerAccepted(int count) => Assert.That(this._accepted, Is.EqualTo(count));

  [Then("^the file in the caller's home directory does not exist$")]
  public void OutsideFileIsAbsent() => Assert.That(File.Exists(this._outside), Is.False);

  [When("^a plan with the probe invoked as " + StepText.Quoted + " runs as untrusted$")]
  public async Task UntrustedProbe(string arguments) {
    this.ProbeInvoked(arguments);
    this._result = await this.RunAsync(this._execute!, TrustLevel.Untrusted);
  }

  [Then("^no process was started$")]
  public void NoProcessStarted() {
    Assert.That(this._sandbox.Commands, Is.Empty);
    Assert.That(this.Result.WorkingDirectory, Is.Null);
  }

  [Given("^the expected output " + StepText.Quoted + " and the actual output " + StepText.Quoted + "$")]
  public void Outputs(string expected, string actual) {
    this._expected = Encoding.UTF8.GetBytes(StepText.Unescape(expected));
    this._actual = Encoding.UTF8.GetBytes(StepText.Unescape(actual));
  }

  [When(@"^they are compared in (\w+) mode$")]
  public void Compared(ComparisonMode mode) => this._comparison = OutputComparison.Compare(this._expected, this._actual, mode);

  [Then(@"^the first difference is at byte offset (\d+) with expected 0x([0-9A-F]{2}) and actual 0x([0-9A-F]{2})$")]
  public void FirstDifference(long offset, string expected, string actual)
    => Assert.That(this._comparison, Is.EqualTo(new ComparisonResult(false, offset, byte.Parse(expected, NumberStyles.HexNumber, CultureInfo.InvariantCulture), byte.Parse(actual, NumberStyles.HexNumber, CultureInfo.InvariantCulture))));

}
