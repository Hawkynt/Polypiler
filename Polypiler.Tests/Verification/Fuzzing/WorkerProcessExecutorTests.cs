using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Fuzzing;
using Polypiler.Tooling.Fuzz;

namespace Polypiler.Tests.Verification.Fuzzing;

/// <summary>
/// Supervision in a real <c>polypiler-tooling fuzz worker</c> process started with this test assembly:
/// hangs, worker death, restarts and the time budget. Every test starts processes and waits out
/// budgets, so the fixture is in the slow tier.
/// </summary>
[TestFixture, Category("Slow")]
public sealed class WorkerProcessExecutorTests {

  private static readonly string _assembly = typeof(DemoMarkerTarget).Assembly.Location;

  private static readonly FuzzBudget _short = new(TimeSpan.FromMilliseconds(500), 64L * 1024 * 1024, 65536);

  private static IFuzzExecutor Worker(IFuzzTarget target) => FuzzCommand.WorkerExecutor(target, [_assembly]);

  [Test]
  public void AWorker_ReportsCrashesAndCleanInputsWithoutRestarting() {
    using var executor = Worker(new DemoMarkerTarget());
    Assert.That(executor.Starts, Is.Zero);
    Assert.That(executor.Execute([1, 2, 3], _short).IsFinding, Is.False);
    var crash = executor.Execute("xPLYx"u8.ToArray(), _short);
    Assert.That(crash.Signature, Is.EqualTo($"crash:System.InvalidOperationException@{typeof(DemoMarkerTarget).FullName}.Execute"));
    Assert.That(executor.Execute([], _short).IsFinding, Is.False);
    Assert.That(executor.Starts, Is.EqualTo(1));
  }

  /// <summary>
  /// A crash keeps its signature once the call to the target is hot: the JIT may then devirtualize the
  /// call and inline the target into its caller, which would take the target's frame out of the trace
  /// and report the same crash twice under two signatures.
  /// </summary>
  [Test]
  public void TheSignature_StaysTheSameWhenTheCallGetsHot() {
    using var executor = Worker(new DemoZeroTarget());
    var signatures = new HashSet<string>(StringComparer.Ordinal);
    var watch = Stopwatch.StartNew();
    for (var round = 0; round < 5000 && watch.Elapsed < TimeSpan.FromSeconds(2); ++round)
      signatures.Add(executor.Execute([0], FuzzBudget.Default).Signature);
    Assert.That(signatures, Is.EqualTo(new[] { $"crash:System.ArgumentException@{typeof(DemoZeroTarget).FullName}.Execute" }));
  }

  [Test]
  public void AnInputThatNeverReturns_IsAHangAndTheNextInputGetsANewWorker() {
    using var executor = Worker(new DemoHangTarget());
    var hang = executor.Execute([0xFF], _short);
    Assert.That((hang.Kind, hang.Signature), Is.EqualTo(((FindingKind?)FindingKind.Hang, "hang")));
    Assert.That(hang.Elapsed, Is.GreaterThanOrEqualTo(_short.PerInput));
    Assert.That(executor.Execute([0x01], _short).IsFinding, Is.False);
    Assert.That(executor.Starts, Is.EqualTo(2));
  }

  /// <summary>Test design 6: a worker that dies is a crash named after its exit code, and the run goes on.</summary>
  [Test]
  public void AWorkerThatDies_IsACrashWithItsExitCode() {
    using var executor = Worker(new DemoFailFastTarget());
    var death = executor.Execute("F"u8.ToArray(), FuzzBudget.Default);
    Assert.That(death.Kind, Is.EqualTo(FindingKind.Crash));
    Assert.That(death.Signature, Does.Match("^process-exit:-?[0-9]+$"));
    Assert.That(executor.Execute("A"u8.ToArray(), FuzzBudget.Default).IsFinding, Is.False);
    Assert.That(executor.Starts, Is.EqualTo(2));
  }

  /// <summary>Test design 10: half a second inside a 1 s budget passes; one and a half seconds is a hang.</summary>
  [Test]
  public void TheTimeBudget_JustInsideAndOutside() {
    using var executor = Worker(new DemoSleepTarget());
    Assert.That(executor.Execute([50], FuzzBudget.Default).IsFinding, Is.False);
    Assert.That(executor.Execute([150], FuzzBudget.Default).Kind, Is.EqualTo(FindingKind.Hang));
  }

  [Test]
  public void AMismatchInTheWorker_IsReportedAsOne() {
    using var executor = Worker(new DemoMismatchTarget());
    var outcome = executor.Execute("xMx"u8.ToArray(), FuzzBudget.Default);
    Assert.That((outcome.Kind, outcome.Signature), Is.EqualTo(((FindingKind?)FindingKind.Mismatch, $"mismatch:{typeof(FuzzMismatchException).FullName}@{typeof(DemoMismatchTarget).FullName}.Execute")));
  }

  /// <summary>
  /// The <c>afl-worker</c> process outside AFL++: SharpFuzz then runs the target once on standard
  /// input, so a crashing input ends the process with an error and a clean one with 0.
  /// </summary>
  [TestCase("abc", true)]
  [TestCase("xPLYx", false)]
  public void TheAflWorkerProcess_RunsOneInputWithoutAflPlusPlus(string input, bool clean) {
    var command = FuzzCommand.WorkerCommand(AppContext.BaseDirectory, OperatingSystem.IsWindows());
    var info = new ProcessStartInfo(command[0]) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in command.Skip(1).Concat(["fuzz", "afl-worker", "--target", "demo-marker", "--assembly", _assembly, "--probe", AppContext.BaseDirectory]))
      info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    process.StandardInput.Write(input);
    process.StandardInput.Close();
    var error = process.StandardError.ReadToEndAsync();
    process.StandardOutput.ReadToEnd();
    Assert.That(process.WaitForExit(TimeSpan.FromSeconds(60)), Is.True);
    Assert.That(process.ExitCode == 0, Is.EqualTo(clean), error.Result);
  }

  [Test]
  public void AllocationInTheWorker_IsMeasuredThere() {
    using var executor = Worker(new DemoAllocTarget());
    var outcome = executor.Execute([1], FuzzBudget.Default);
    Assert.That(outcome.Kind, Is.EqualTo(FindingKind.Allocation));
    Assert.That(outcome.AllocatedBytes, Is.GreaterThanOrEqualTo(DemoAllocTarget.Allocation));
  }

  [Test]
  public void AWorkerThatCannotLoadItsTarget_FailsToStartWithWhatItSaid() {
    using var executor = new WorkerProcessExecutor(() => {
      var command = FuzzCommand.WorkerCommand(AppContext.BaseDirectory, OperatingSystem.IsWindows());
      var info = new ProcessStartInfo(command[0]);
      foreach (var argument in command.Skip(1).Concat(["fuzz", "worker", "--target", "no-such-target"]))
        info.ArgumentList.Add(argument);
      return info;
    });
    Assert.That(() => executor.Execute([1], FuzzBudget.Default),
      Throws.InstanceOf<FuzzWorkerException>().With.Message.Contains("PLY0003: unknown fuzz target 'no-such-target'"));
    Assert.That(executor.Starts, Is.EqualTo(1));
  }

  [Test]
  public void AWorkerThatCannotBeStarted_IsNamed() {
    using var executor = new WorkerProcessExecutor(() => new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "no-such-worker")));
    Assert.That(() => executor.Execute([1], FuzzBudget.Default), Throws.InstanceOf<FuzzWorkerException>().With.Message.Contains("cannot be started"));
    Assert.That(() => executor.Execute(null!, FuzzBudget.Default), Throws.ArgumentNullException);
    Assert.That(() => executor.Execute([], null!), Throws.ArgumentNullException);
  }

  /// <summary>The third scenario through the engine: the hang at iteration 0, then two more iterations in a new worker.</summary>
  [Test]
  public void TheEngine_ContinuesAfterAHang() {
    using var workspace = new FuzzWorkspace();
    var engine = new FuzzEngine(Worker, workspace.Artifacts);
    var report = engine.Run(new DemoHangTarget(), workspace.Corpus("demo-hang", ("seed.bin", [0xFF])), 1, 3, FuzzBudget.Default);
    var hang = report.Findings.First();
    Assert.That((hang.Kind, hang.Iteration, hang.Minimized), Is.EqualTo((FindingKind.Hang, 0L, new byte[] { 0xFF })));
    Assert.That(report.Findings, Has.Count.EqualTo(1));
    Assert.That(report.Executed, Is.EqualTo(3));
    Assert.That(report.WorkerStarts, Is.GreaterThanOrEqualTo(2));
  }

  /// <summary>Test design 6 through the engine: the death is one finding, and the iterations after it run.</summary>
  [Test]
  public void TheEngine_ContinuesAfterAWorkerDied() {
    using var workspace = new FuzzWorkspace();
    var target = new DemoFailFastTarget();
    var report = new FuzzEngine(Worker, workspace.Artifacts).Run(target, workspace.Corpus(target.Id, ("a.bin", "Fx"u8.ToArray()), ("b.bin", "A"u8.ToArray())), 1, 2, FuzzBudget.Default);
    var death = report.Findings.Single();
    Assert.That((death.Iteration, death.Minimized), Is.EqualTo((0L, "F"u8.ToArray())));
    Assert.That(death.Signature, Does.StartWith("process-exit:"));
    Assert.That(report.Executed, Is.EqualTo(2));
  }

}
