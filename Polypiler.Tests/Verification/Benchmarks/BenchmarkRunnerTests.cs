using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Benchmarks;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using static Polypiler.Tests.Verification.Benchmarks.BenchTesting;

namespace Polypiler.Tests.Verification.Benchmarks;

/// <summary>
/// The benchmark runner on a scripted cross-language runner: the times are chosen, never measured, so
/// that no test depends on how fast this machine is.
/// </summary>
[TestFixture]
public sealed class BenchmarkRunnerTests {

  private const string Root = "tests/fixtures/bench";
  private const string Expected = "499999500000";

  private readonly DiagnosticBag _bag = new();

  private BenchmarkRunner Runner(ICrossLanguageRunner runner, Func<string, string?>? version = null)
    => new(runner, new BenchmarkHost(PrototypeFixture.RepositoryRoot, "abc123", version ?? (_ => "24.22.2")) {
      Environment = toolchains => Machine(node: toolchains["node"]),
      Placeholders = new Dictionary<string, string> { ["{configuration}"] = "Release" }
    }, this._bag);

  private static BenchmarkDefinition Definition(string extra = "")
    => BenchmarkDefinition.Parse(File.ReadAllText(Path.Combine(FixtureFolder, "sum-loop.bench.yaml")) + extra, "sum-loop.bench.yaml");

  private string[] Codes => [.. this._bag.Items.Select(diagnostic => diagnostic.Code)];

  /// <summary>Test design 1: one warm-up run and five measured ones, the warm-up's sample discarded.</summary>
  [Test]
  public void AProcessBenchmark_RecordsTheMeasuredRunsOnly() {
    var runner = new ScriptedBenchRunner(Completed(Expected + "\n", 999, 999), Completed(Expected + "\n", 10, 5), Completed(Expected + "\n", 12, 6), Completed(Expected, 11, 5), Completed(Expected + "\r\n", 50, 25), Completed(Expected + "\n", 13, 6));
    var result = Runner(runner).Run(Definition(), "default")!;
    Assert.That(this._bag.Items, Is.Empty);
    Assert.That(runner.Plans, Has.Count.EqualTo(6));
    Assert.That(result.Samples.Select(sample => sample.WallNs), Is.EqualTo(new[] { 10_000_000L, 12_000_000, 11_000_000, 50_000_000, 13_000_000 }));
    Assert.That(result.Samples.Select(sample => sample.CpuNs), Is.EqualTo(new long?[] { 5_000_000, 6_000_000, 5_000_000, 25_000_000, 6_000_000 }));
    Assert.That(result.Stats["wallNs"], Is.EqualTo(new MetricStatistics(12_000_000, 1_000_000, 10_000_000, 50_000_000)));
    Assert.That((result.Id, result.Variant, result.Commit, result.Environment.Toolchains["node"]), Is.EqualTo(("sum-loop", "default", "abc123", "24.22.2")));
    Assert.That((result.BuildNs, result.BinaryBytes, result.AllocatedBytes, result.Allocations), Is.EqualTo(((long?)null, (long?)null, (long?)null, (long?)null)));
  }

  [Test]
  public void ThePlan_CopiesTheSources_AndRunsTheToolchainWithTheArguments() {
    var runner = new ScriptedBenchRunner(Completed(Expected, 1));
    Runner(runner).Run(Definition(), "default");
    var plan = runner.Plans[0];
    Assert.That(plan.Files.Keys, Is.EqualTo(new[] { "tests/fixtures/bench/sum-loop.js" }));
    Assert.That(plan.Files["tests/fixtures/bench/sum-loop.js"].ToArray(), Is.EqualTo(File.ReadAllBytes(Path.Combine(FixtureFolder, "sum-loop.js"))));
    Assert.That((plan.Execute.Toolchain, plan.Build.Count, plan.Trust), Is.EqualTo(("node", 0, TrustLevel.Trusted)));
    Assert.That(plan.Execute.Arguments, Is.EqualTo(new[] { "tests/fixtures/bench/sum-loop.js" }));
  }

  /// <summary>Scenario "Size metrics are the byte sizes on disk".</summary>
  [Test]
  public void TheSourceSize_IsTheSizeOnDisk() {
    var definition = Definition() with { Sizes = new([$"{Root}/size/abc.txt"], []) };
    Assert.That(Runner(new ScriptedBenchRunner(Completed(Expected, 1))).Run(definition, "default")!.SourceBytes, Is.EqualTo(3));
  }

  [Test]
  public void ASourceSizeDirectory_CountsEveryFileBelowIt() {
    var definition = Definition() with { Sizes = new([$"{Root}/size"], []) };
    Assert.That(Runner(new ScriptedBenchRunner(Completed(Expected, 1))).Run(definition, "default")!.SourceBytes, Is.EqualTo(3));
  }

  [Test]
  public void AMissingSizePath_IsReported() {
    var definition = Definition() with { Sizes = new([$"{Root}/size/none.txt"], []) };
    Assert.That(Runner(new ScriptedBenchRunner(Completed(Expected, 1))).Run(definition, "default"), Is.Null);
    Assert.That(this._bag.Items.Single().Message, Is.EqualTo($"'sizes.source' names '{Root}/size/none.txt', which does not exist"));
  }

  [Test]
  public void AMissingSource_IsReported_BeforeAnythingRuns() {
    var runner = new ScriptedBenchRunner(Completed(Expected, 1));
    Assert.That(Runner(runner).Run(Definition() with { Sources = ["no/such.js"] }, "default"), Is.Null);
    Assert.That((this.Codes.Single(), runner.Plans.Count), Is.EqualTo(("PLY0078", 0)));
    Assert.That(this._bag.ExitCode, Is.EqualTo(ExitCode.EnvironmentError));
  }

  /// <summary>Test design 10: build time and binary size come from the runner's result.</summary>
  [Test]
  public void TheBuildTimeAndTheBinarySize_AreRecorded() {
    var collected = new Dictionary<string, ReadOnlyMemory<byte>> { ["out/a.bin"] = new byte[100], ["out/sub/b.bin"] = new byte[20], ["output.txt"] = new byte[7], ["single.exe"] = new byte[5] };
    var runner = new ScriptedBenchRunner(Completed(Expected, 1, buildMs: 40, collected: collected), Completed(Expected, 1, buildMs: 30, collected: collected), Completed(Expected, 1, buildMs: 50, collected: collected));
    var definition = Definition() with { Build = ["--check", "{root}/x"], Warmup = 0, Runs = 3, Sizes = new([], ["out/", "single.exe", "absent"]) };
    var result = Runner(runner).Run(definition, "default")!;
    Assert.That((result.BuildNs, result.BinaryBytes), Is.EqualTo((40_000_000L, 125L)));
    Assert.That(runner.Plans[0].Collect, Is.EqualTo(new[] { "out/", "single.exe", "absent" }));
    Assert.That(runner.Plans[0].Build.Single().Arguments, Is.EqualTo(new[] { "--check", PrototypeFixture.RepositoryRoot + "/x" }));
  }

  /// <summary>Test design 11: each variant runs with its own arguments and environment.</summary>
  [Test]
  public void AVariant_AddsItsArgumentsAndEnvironment() {
    var definition = Definition("variants:\n  - { name: opt-none, args: [--opt, none] }\n  - { name: opt-speed, args: [--opt, '{configuration}'], env: { OPT: speed } }\n");
    var runner = new ScriptedBenchRunner(Completed(Expected, 1));
    Assert.That(Runner(runner).Run(definition, "opt-speed")!.Variant, Is.EqualTo("opt-speed"));
    Assert.That(runner.Plans[0].Execute.Arguments, Is.EqualTo(new[] { "tests/fixtures/bench/sum-loop.js", "--opt", "Release" }));
    Assert.That(runner.Plans[0].Execute.Environment, Is.EqualTo(new Dictionary<string, string> { ["OPT"] = "speed" }));
    Assert.That(() => Runner(runner).Run(definition, "default"), Throws.ArgumentException);
    Assert.That(() => Runner(runner).Run(null!, "default"), Throws.ArgumentNullException);
  }

  /// <summary>Test design 5: a single measured run without warm-up.</summary>
  [Test]
  public void OneRunWithoutWarmup_IsOneSample() {
    var runner = new ScriptedBenchRunner(Completed(Expected, 7));
    var result = Runner(runner).Run(Definition() with { Warmup = 0, Runs = 1 }, "default")!;
    Assert.That((runner.Plans.Count, result.Samples.Single().WallNs), Is.EqualTo((1, 7_000_000L)));
  }

  [Test]
  public void TheStandardInput_IsRead() {
    var runner = new ScriptedBenchRunner(Completed(Expected, 1));
    Runner(runner).Run(Definition() with { Stdin = $"{Root}/size/abc.txt" }, "default");
    Assert.That(Encoding.UTF8.GetString(runner.Plans[0].Execute.Stdin.Span), Is.EqualTo("abc"));
  }

  /// <summary>Test design 14: extra metrics supplied by the caller are stored.</summary>
  [Test]
  public void ExtraMetrics_AreStored()
    => Assert.That(Runner(new ScriptedBenchRunner(Completed(Expected, 1))).Run(Definition(), "default", new Dictionary<string, long> { ["instructions"] = 42 })!.Extra["instructions"], Is.EqualTo(42));

  [Test]
  public void AnUnknownToolchainVersion_IsRecordedAsUnknown()
    => Assert.That(Runner(new ScriptedBenchRunner(Completed(Expected, 1)), _ => null).Run(Definition(), "default")!.Environment.Toolchains["node"], Is.EqualTo("unknown"));

  /// <summary>Test design 16: a wrong output is no measurement, reported as PLY0051 with exit code 5.</summary>
  [Test]
  public void AWrongOutput_IsNoMeasurement() {
    var runner = new ScriptedBenchRunner(Completed(Expected, 1), Completed("0\n", 1));
    Assert.That(Runner(runner).Run(Definition(), "default"), Is.Null);
    var diagnostic = this._bag.Items.Single();
    Assert.That((diagnostic.Code, diagnostic.ExitCode, this._bag.ExitCode), Is.EqualTo(("PLY0051", ExitCode.SemanticMismatch, ExitCode.SemanticMismatch)));
    Assert.That(diagnostic.Message, Is.EqualTo("benchmark 'sum-loop/default': the program printed '0', expected '499999500000'; nothing is recorded"));
    Assert.That(runner.Plans, Has.Count.EqualTo(2), "the measurement stops at the first wrong output");
  }

  [Test]
  public void WithoutExpectStdout_TheOutputIsNotChecked()
    => Assert.That(Runner(new ScriptedBenchRunner(Completed("anything", 1))).Run(Definition() with { ExpectStdout = null }, "default"), Is.Not.Null);

  /// <summary>Test design 17: a run that exits non-zero records nothing.</summary>
  [Test]
  public void ANonZeroExit_IsNoMeasurement() {
    Assert.That(Runner(new ScriptedBenchRunner(Completed(Expected, 1, exitCode: 3))).Run(Definition(), "default"), Is.Null);
    var diagnostic = this._bag.Items.Single();
    Assert.That((diagnostic.Code, diagnostic.Message, diagnostic.Cause), Is.EqualTo(("PLY0079", "benchmark 'sum-loop/default': the program exited with 3; nothing is recorded", "it broke")));
  }

  [Test]
  public void ANonZeroExitWithoutStandardError_HasNoCause() {
    var silent = Completed(Expected, 1, exitCode: 3) with { Stderr = ReadOnlyMemory<byte>.Empty };
    Assert.That(Runner(new ScriptedBenchRunner(silent)).Run(Definition(), "default"), Is.Null);
    Assert.That(this._bag.Items.Single().Cause, Is.Null);
  }

  /// <summary>Test design 17 and 18: the runner's own diagnostic is passed through, for a time limit as for a missing toolchain.</summary>
  [TestCase(RunOutcome.TimedOut, "PLY0031")]
  [TestCase(RunOutcome.Refused, "PLY0021")]
  public void ARunTheRunnerEnded_PassesItsDiagnosticThrough(RunOutcome outcome, string code) {
    var diagnostic = new Diagnostic(code, DiagnosticSeverity.Error, "from the runner") { ExitCode = ExitCode.EnvironmentError };
    var failed = Completed("", 1) with { Outcome = outcome, ExitCode = -1, Diagnostics = [diagnostic] };
    Assert.That(Runner(new ScriptedBenchRunner(failed)).Run(Definition(), "default"), Is.Null);
    Assert.That(this._bag.Items, Is.EqualTo(new[] { diagnostic }));
  }

  /// <summary>Test design 2: a dotnet benchmark runs once and records its export's iterations and allocations.</summary>
  [Test]
  public void ADotNetBenchmark_ConvertsItsExport() {
    var export = """{"Benchmarks":[{"Statistics":{"OriginalValues":[100.0,120.0,110.0]},"Memory":{"BytesAllocatedPerOperation":4312}}]}""";
    var collected = new Dictionary<string, ReadOnlyMemory<byte>> { [$"{BenchmarkDotNetExport.ResultsFolder}/Cli-report-full.json"] = Encoding.UTF8.GetBytes(export) };
    var runner = new ScriptedBenchRunner(Completed("", 3000, collected: collected));
    var definition = Definition() with { Kind = BenchmarkKind.DotNet, ExpectStdout = null };
    var result = Runner(runner).Run(definition, "default")!;
    Assert.That(runner.Plans, Has.Count.EqualTo(1));
    Assert.That(runner.Plans[0].Collect, Is.EqualTo(new[] { BenchmarkDotNetExport.ResultsFolder }));
    Assert.That(result.Samples.Select(sample => sample.WallNs), Is.EqualTo(new[] { 100L, 120, 110 }));
    Assert.That((result.AllocatedBytes, result.Stats.ContainsKey("cpuNs")), Is.EqualTo(((long?)4312, false)));
  }

  [Test]
  public void ADotNetBenchmarkWithoutExport_IsReported() {
    var definition = Definition() with { Kind = BenchmarkKind.DotNet, ExpectStdout = null };
    Assert.That(Runner(new ScriptedBenchRunner(Completed("", 1))).Run(definition, "default"), Is.Null);
    Assert.That(this._bag.Items.Single().Message, Does.Contain("BenchmarkDotNet wrote no '*-report-full.json'"));
  }

}
