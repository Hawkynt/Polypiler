using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Rebuild;
using Polypiler.Core.Verification.Running;
using static Polypiler.Tests.Verification.Rebuild.TreeHashTests;

namespace Polypiler.Tests.Verification.Rebuild;

/// <summary>A runner that hands back scripted results, one per run, and records the plans it was given.</summary>
internal sealed class ScriptedRebuildRunner(params RunResult[] results) : ICrossLanguageRunner {

  public List<RunPlan> Plans { get; } = [];

  public Task<RunResult> RunAsync(RunPlan plan, CancellationToken cancellationToken = default) {
    this.Plans.Add(plan);
    return Task.FromResult(results[this.Plans.Count - 1]);
  }

  /// <summary>A completed run with exit code 0 whose working directory held the given files.</summary>
  public static RunResult Wrote(params (string Path, string Content)[] files)
    => Ended(RunOutcome.Completed, 0) with { Collected = Tree(files) };

  /// <summary>A run that ended as given.</summary>
  public static RunResult Ended(RunOutcome outcome, int exitCode, string stdout = "", string stderr = "", params Diagnostic[] diagnostics)
    => new(outcome, exitCode, Encoding.UTF8.GetBytes(stdout), Encoding.UTF8.GetBytes(stderr), TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited, diagnostics);

}

/// <summary><see cref="RebuildCheck"/> on scripted runs: the plans it hands the runner, the comparison, and a run that does not complete.</summary>
[TestFixture]
public sealed class RebuildCheckTests {

  private static readonly (string, string)[] _stable = [("out/a.txt", "hello\n"), ("out/b/c.txt", "")];

  private static RebuildRequest Request(int runs = 2) => new("out", new ProcessSpec("dotnet-sdk", "gen.dll", "stable", "{output}"), runs);

  private static RebuildResult Check(ScriptedRebuildRunner runner, RebuildRequest? request = null)
    => new RebuildCheck(runner).RunAsync(request ?? Request()).GetAwaiter().GetResult();

  private static string[] Messages(RebuildResult result) => [.. result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")];

  /// <summary>Test design 1: two identical runs.</summary>
  [Test]
  public void IdenticalRuns_AreDeterministic() {
    var runner = new ScriptedRebuildRunner(ScriptedRebuildRunner.Wrote(_stable), ScriptedRebuildRunner.Wrote(_stable));
    var result = Check(runner);
    Assert.That((result.Deterministic, result.Diagnostics.Count), Is.EqualTo((true, 0)));
    Assert.That(result.Trees.Select(tree => tree.Sha256), Is.EqualTo(new[] { StableHash, StableHash }));
  }

  /// <summary>Each run is a fresh plan: the placeholder replaced, the output collected, trusted, the inputs and limits given.</summary>
  [Test]
  public void ThePlan_RunsTheCommandOnTheOutputDirectory() {
    var runner = new ScriptedRebuildRunner(ScriptedRebuildRunner.Wrote(_stable), ScriptedRebuildRunner.Wrote(_stable));
    var limits = new RunLimits(TimeSpan.FromSeconds(3), 1 << 20, 100, 4);
    var inputs = Tree(("gen.dll", "x"));
    var command = new ProcessSpec("dotnet-sdk", ["gen.dll", "{output}", "{output}/f.irm", "{output}{output}"], new byte[] { 1 }) { Environment = new Dictionary<string, string> { ["A"] = "b" } };
    Check(runner, new RebuildRequest("out/", command) { Inputs = inputs, Limits = limits });
    Assert.That(runner.Plans, Has.Count.EqualTo(2));
    var plan = runner.Plans[0];
    Assert.That(plan.Execute.Arguments, Is.EqualTo(new[] { "gen.dll", "out", "out/f.irm", "outout" }));
    Assert.That((plan.Execute.Toolchain, plan.Execute.Stdin.ToArray()[0], plan.Execute.Environment["A"]), Is.EqualTo(("dotnet-sdk", (byte)1, "b")));
    Assert.That((plan.Collect.Single(), plan.Build.Count, plan.Trust, plan.Limits), Is.EqualTo(("out", 0, TrustLevel.Trusted, limits)));
    Assert.That(plan.Files.Keys, Is.EqualTo(new[] { "gen.dll" }));
  }

  [Test]
  public void ARequest_HasDefaults() {
    var request = Request();
    Assert.That((request.Runs, request.Limits, request.Ignore, request.Inputs.Count), Is.EqualTo((2, RunLimits.Default, RebuildIgnore.Empty, 0)));
  }

  /// <summary>Only what lies below the output directory is the tree; a file merely sharing its prefix is not.</summary>
  [Test]
  public void FilesOutsideTheOutput_AreNotPartOfTheTree() {
    var runner = new ScriptedRebuildRunner(
      ScriptedRebuildRunner.Wrote([.. _stable, ("outside.txt", "1"), ("output/x", "1")]),
      ScriptedRebuildRunner.Wrote([.. _stable, ("outside.txt", "2"), ("output/x", "2")]));
    Assert.That(Check(runner).Trees[0].Sha256, Is.EqualTo(StableHash));
  }

  /// <summary>Test designs 3 and 14: every difference is a PLY0036, run 1 against each later run.</summary>
  [Test]
  public void EveryDifference_IsReported() {
    var runner = new ScriptedRebuildRunner(
      ScriptedRebuildRunner.Wrote([.. _stable, ("out/stamp.txt", "100"), ("out/one.txt", "")]),
      ScriptedRebuildRunner.Wrote([.. _stable, ("out/stamp.txt", "101")]),
      ScriptedRebuildRunner.Wrote([.. _stable, ("out/stamp.txt", "100"), ("out/one.txt", ""), ("out/three.txt", "")]));
    var result = Check(runner, Request(3));
    Assert.That(result.Deterministic, Is.False);
    Assert.That(result.Trees, Has.Count.EqualTo(3));
    Assert.That(Messages(result), Is.EqualTo(new[] {
      "PLY0036: rebuild not deterministic: 'one.txt' exists only in run 1",
      "PLY0036: rebuild not deterministic: 'stamp.txt' differs at byte 2",
      "PLY0036: rebuild not deterministic: 'three.txt' exists only in run 3"
    }));
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.ExitCode), Has.All.EqualTo(ExitCode.EnvironmentError));
  }

  /// <summary>Test design 5: an ignored file and an ignored manifest pointer pass; the same drift elsewhere does not.</summary>
  [Test]
  public void WhatTheListIgnores_Passes() {
    var ignore = new RebuildIgnore([new("*.log", "log")], [new("/verification/durationMs", "time")]);
    var runner = new ScriptedRebuildRunner(
      ScriptedRebuildRunner.Wrote(("out/run.log", "1"), ("out/polypiler.manifest.json", """{"verification":{"durationMs":1}}""")),
      ScriptedRebuildRunner.Wrote(("out/run.log", "2"), ("out/polypiler.manifest.json", """{"verification":{"durationMs":2}}""")));
    Assert.That(Check(runner, Request() with { Ignore = ignore }).Deterministic, Is.True);
    var drifting = new ScriptedRebuildRunner(
      ScriptedRebuildRunner.Wrote(("out/polypiler.manifest.json", """{"outputs":[{"sha256":"aa"}]}""")),
      ScriptedRebuildRunner.Wrote(("out/polypiler.manifest.json", """{"outputs":[{"sha256":"ab"}]}""")));
    Assert.That(Messages(Check(drifting, Request() with { Ignore = ignore })), Is.EqualTo(new[] { "PLY0036: rebuild not deterministic: 'polypiler.manifest.json' differs at byte 24" }));
  }

  /// <summary>Test design 19: a manifest that is not JSON passes when its bytes agree and fails when they do not.</summary>
  [Test]
  public void AManifestThatIsNotJson_IsComparedAsBytes() {
    var truncated = ("out/polypiler.manifest.json", """{"outputs":[""");
    Assert.That(Check(new ScriptedRebuildRunner(ScriptedRebuildRunner.Wrote(truncated), ScriptedRebuildRunner.Wrote(truncated))).Deterministic, Is.True);
    var other = ("out/polypiler.manifest.json", """{"outputs":{""");
    Assert.That(Messages(Check(new ScriptedRebuildRunner(ScriptedRebuildRunner.Wrote(truncated), ScriptedRebuildRunner.Wrote(other)))),
      Is.EqualTo(new[] { "PLY0036: rebuild not deterministic: 'polypiler.manifest.json' differs at byte 11" }));
  }

  /// <summary>Test design 17: a run over a limit passes the runner's diagnostic through, and nothing more runs or is compared.</summary>
  [Test]
  public void ARunThatDoesNotComplete_EndsTheCheck() {
    var timedOut = Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 1000L);
    var runner = new ScriptedRebuildRunner(ScriptedRebuildRunner.Wrote(_stable), ScriptedRebuildRunner.Ended(RunOutcome.TimedOut, -1, diagnostics: timedOut), ScriptedRebuildRunner.Wrote(_stable));
    var result = Check(runner, Request(3));
    Assert.That((result.Trees.Count, result.Deterministic, runner.Plans.Count), Is.EqualTo((0, false, 2)));
    Assert.That(result.Diagnostics, Is.EqualTo(new[] { timedOut }));
    Assert.That(result.Diagnostics[0].ExitCode, Is.EqualTo(ExitCode.BudgetExceeded));
  }

  /// <summary>Test design 16: the command fails in run 2; PLY0030 with what it printed, nothing compared.</summary>
  [TestCase("", "boom\n", "boom")]
  [TestCase("out\n", "err\n", "err\nout")]
  [TestCase("", "", "the command printed nothing")]
  public void ACommandThatFails_IsABuildFailure(string stdout, string stderr, string cause) {
    var runner = new ScriptedRebuildRunner(ScriptedRebuildRunner.Wrote(_stable), ScriptedRebuildRunner.Ended(RunOutcome.Completed, 3, stdout, stderr));
    var result = Check(runner);
    Assert.That((result.Trees.Count, result.Diagnostics.Count), Is.EqualTo((0, 1)));
    var failure = result.Diagnostics[0];
    Assert.That((failure.Code, failure.Message, failure.Cause, failure.ExitCode), Is.EqualTo(("PLY0030", "target build failed: 'dotnet-sdk gen.dll stable out' exited with 3", cause, ExitCode.TargetBuildFailure)));
  }

  /// <summary>Test design 10: fewer than two runs is refused; the output stays inside the working directory.</summary>
  [Test]
  public void AnInvalidRequest_IsRefused() {
    var command = new ProcessSpec("dotnet-sdk", "x");
    Assert.That(() => new RebuildRequest("out", command, 1), Throws.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(() => new RebuildRequest("out", command, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(new RebuildRequest("out", command, 2).Runs, Is.EqualTo(2));
    Assert.That(new RebuildRequest("a\\b\\", command).Output, Is.EqualTo("a/b"));
    foreach (var output in new[] { "", "/abs", "../up", "a/../../b", null })
      Assert.That(() => new RebuildRequest(output!, command), Throws.ArgumentException, output);
    Assert.That(() => new RebuildRequest("out", null!), Throws.ArgumentNullException);
    Assert.That(() => new RebuildCheck(new ScriptedRebuildRunner()).RunAsync(null!).GetAwaiter().GetResult(), Throws.ArgumentNullException);
    Assert.That(() => RebuildDiagnostics.Difference(null!, 2), Throws.ArgumentNullException);
  }

}
