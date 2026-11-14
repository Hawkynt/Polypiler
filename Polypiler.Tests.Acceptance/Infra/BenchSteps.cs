using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Benchmarks;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Toolchains;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/infra-bench.feature</c>. A measurement starts the real
/// <c>polypiler-tooling bench run</c> in the scenario's workspace, which holds the toolchain lock, the
/// definition under <c>tests/benchmarks/</c> and its sources as a checkout does; the program runs in
/// this host's sandbox. Comparisons start <c>bench compare</c> on result files the steps write.
/// </summary>
[Binding, Scope(Feature = "Benchmark harness for speed, memory and code size")]
public sealed class BenchSteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Id = "sum-loop";
  private const string Baseline = "baseline";
  private const string Current = "current";

  private long[] _samples = [];
  private (long Median, long Mad) _statistics;
  private string? _budgets;

  private static string Repository(string path) => Path.Combine(PrototypeFixture.RepositoryRoot, path);

  private string DefinitionPath => workspace.Resolve($"tests/benchmarks/{Id}{BenchmarkDefinition.Extension}");

  private BenchmarkResult ResultFile(string path) {
    var full = workspace.Resolve(path);
    Assert.That(File.Exists(full), Is.True, log.Last.Output);
    return BenchmarkResult.Load(full);
  }

  /// <summary>A checkout in the workspace: the lock, the definition (as its ID names it) and the program; node must be installed.</summary>
  private BenchmarkDefinition Checkout(string definition, Func<string, string>? change = null) {
    // The harness measures with whatever node is installed, as the runner does; its version goes into the environment.
    if (Polypiler.Tests.Verification.Running.Support.RunnerTesting.Toolchains.Resolve("node").Problem is { } missing)
      Assert.Inconclusive(missing.ToString());
    workspace.Copy(Repository(ToolchainLock.RepositoryPath), ToolchainLock.RepositoryPath);
    workspace.Copy(Repository("tests/fixtures/bench/sum-loop.js"), "tests/fixtures/bench/sum-loop.js");
    workspace.Copy(Repository("tests/fixtures/bench/size/abc.txt"), "tests/fixtures/bench/size/abc.txt");
    var text = File.ReadAllText(Repository(definition));
    workspace.Write($"tests/benchmarks/{Id}{BenchmarkDefinition.Extension}", change?.Invoke(text) ?? text);
    return BenchmarkDefinition.Load(this.DefinitionPath);
  }

  [Given("^the benchmark definition " + StepText.Quoted + @" with (\d+) warm-up and (\d+) runs on toolchain " + StepText.Quoted + "$")]
  public void Definition(string path, int warmup, int runs, string toolchain) {
    var definition = this.Checkout(path);
    Assert.That((definition.Warmup, definition.Runs, definition.Toolchain), Is.EqualTo((warmup, runs, toolchain)));
  }

  [Given("^the benchmark definition " + StepText.Quoted + " with \"expectStdout\" changed to " + StepText.Quoted + "$")]
  public void DefinitionExpecting(string path, string expected)
    => Assert.That(this.Checkout(path, text => text.Replace("expectStdout: \"499999500000\"", $"expectStdout: \"{expected}\"", StringComparison.Ordinal)).ExpectStdout, Is.EqualTo(expected));

  [Given("^a definition whose \"sizes\" lists " + StepText.Quoted + " as source$")]
  public void DefinitionWithSize(string path)
    => Assert.That(this.Checkout("tests/fixtures/bench/sum-loop.bench.yaml", text => text.Replace("  source: [tests/fixtures/bench/sum-loop.js]", $"  source: [{path}]", StringComparison.Ordinal)).Sizes.Source, Is.EqualTo(new[] { path }));

  [When("^\"bench run --id (.+)\" is invoked$")]
  public void BenchRun(string id) => log.Add(Toolchain.Tooling(workspace.Path, "bench", "run", "--id", id));

  [When("^the benchmark is run$")]
  public void BenchmarkIsRun() => this.BenchRun(Id);

  [Then("^" + StepText.Quoted + @" contains exactly (\d+) samples$")]
  public void ContainsSamples(string path, int count) => Assert.That(this.ResultFile(path).Samples, Has.Count.EqualTo(count));

  [Then("^every sample has a positive " + StepText.Quoted + ", " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void EverySampleIsPositive(string wall, string cpu, string peak) {
    Assert.That((wall, cpu, peak), Is.EqualTo(("wallNs", "cpuNs", "peakRssBytes")));
    var samples = this.ResultFile($"artifacts/bench/{Id}/default.json").Samples;
    Assert.That(samples, Has.All.Matches<BenchmarkSample>(sample => sample.WallNs > 0 && sample.CpuNs > 0 && sample.PeakRssBytes > 0), string.Join("\n", samples));
  }

  /// <summary>A run that printed anything else would have ended the measurement without a result (PLY0051).</summary>
  [Then("^each run printed " + StepText.Quoted + "$")]
  public void EachRunPrinted(string output) {
    Assert.That(BenchmarkDefinition.Load(this.DefinitionPath).ExpectStdout, Is.EqualTo(output));
    Assert.That((log.Last.Stderr, this.ResultFile($"artifacts/bench/{Id}/default.json").Samples.Count), Is.EqualTo(("", 5)));
  }

  [Then("^the summary row for " + StepText.Quoted + " carries the note " + StepText.Quoted + "$")]
  public void SummaryRow(string id, string note) {
    var row = File.ReadAllLines(workspace.Resolve("artifacts/bench/summary.md")).Single(line => line.StartsWith($"| {id} |", StringComparison.Ordinal));
    Assert.That(row, Does.EndWith($"| {note} |"));
  }

  [Then(@"^the tooling exit code is (\d+)$")]
  public void ExitCode(int code) => Assert.That(log.Last.ExitCode, Is.EqualTo(code), log.Last.Output);

  [Given(@"^the wall-time samples ([\d, ]+) in milliseconds$")]
  public void Samples(string samples) => this._samples = [.. samples.Split(',').Select(text => long.Parse(text.Trim(), CultureInfo.InvariantCulture) * 1_000_000)];

  [When("^the statistics are computed$")]
  public void Compute() => this._statistics = (Statistics.Median(this._samples), Statistics.Mad(this._samples));

  [Then(@"^the median is (\d+) ms and the MAD is (\d+) ms$")]
  public void MedianAndMad(long median, long mad) => Assert.That(this._statistics, Is.EqualTo((median * 1_000_000, mad * 1_000_000)));

  [Then("^" + StepText.Quoted + @" is (\d+)$")]
  public void MetricIs(string metric, long value) => Assert.That(this.ResultFile($"artifacts/bench/{Id}/default.json").Value(metric), Is.EqualTo(value));

  /// <summary>Writes a result of the sum-loop benchmark with one sample of the given wall time.</summary>
  private void WriteResult(string set, long wallMs, int cores) {
    var environment = new BenchmarkEnvironment("linux", "6.8.0", "x64", "Test CPU", cores, new Dictionary<string, string> { ["node"] = "24.22.2" });
    var result = new BenchmarkResult(Id, "default", "0123456789abcdef", environment, [new(wallMs * 1_000_000, wallMs * 900_000, 50_000_000)], null, 171, null, null, null, new Dictionary<string, long>());
    workspace.Write($"{set}/{Id}/default.json", result.ToJson());
  }

  [Given(@"^a baseline result with median wall time (\d+) ms and a current result with median (\d+) ms on the same environment$")]
  public void BaselineAndCurrent(long baseline, long current) {
    this.WriteResult(Baseline, baseline, 8);
    this.WriteResult(Current, current, 8);
  }

  [Given(@"^a baseline result recorded with (\d+) logical cores and a current result recorded with (\d+) logical cores$")]
  public void DifferentCores(int baseline, int current) {
    this.WriteResult(Baseline, 100, baseline);
    this.WriteResult(Current, 100, current);
  }

  [Given("^the test budget file " + StepText.Quoted + @" allowing (\d+) percent regression of " + StepText.Quoted + "$")]
  public void TestBudget(string path, decimal percent, string metric) {
    this._budgets = workspace.Copy(Repository(path), "budgets-test.yaml");
    Assert.That(BudgetFile.Load(this._budgets).Find(Id, "default", metric)?.MaxRegressionPercent, Is.EqualTo(percent));
  }

  [Given("^no budget file$")]
  public void NoBudget() => this._budgets = null;

  [When("^\"bench compare\" is invoked$")]
  public void BenchCompare() {
    string[] budgets = this._budgets is { } file ? ["--budgets", file] : [];
    log.Add(Toolchain.Tooling(workspace.Path, ["bench", "compare", "--baseline", Baseline, "--current", Current, .. budgets]));
  }

  [Then("^the outcome is pass, change (.+)$")]
  public void OutcomePasses(string change) {
    Assert.That(log.Last.ExitCode, Is.Zero, log.Last.Output);
    Assert.That(log.Last.Stdout, Does.Contain($"| {Id} | default | wallNs | 100000000 |").And.Contain($"| {change} | within budget |"));
  }

  [Then(@"^the outcome is (PLY\d{4}), tooling exit code (\d+)$")]
  public void OutcomeFails(string code, int exitCode) {
    Assert.That(log.Last.ExitCode, Is.EqualTo(exitCode), log.Last.Output);
    Assert.That(log.Last.Stderr, Does.Contain($"error {code}: benchmark '{Id}/default': wallNs regressed by"));
  }

  [Then("^the table shows " + StepText.Quoted + " with the note " + StepText.Quoted + "$")]
  public void TableShows(string change, string note)
    => Assert.That(log.Last.Stdout, Does.Contain($"| {Id} | default | wallNs | 100000000 | 500000000 | {change} | {note} |"));

  [Then("^diagnostic PLY0072 names the field " + StepText.Quoted + " with " + StepText.Quoted + " vs " + StepText.Quoted + "$")]
  public void EnvironmentDiffers(string field, string baseline, string current) {
    Assert.That(log.Last.ExitCode, Is.EqualTo(1), log.Last.Output);
    Assert.That(log.Last.Stderr, Does.Contain($"error PLY0072: benchmark '{Id}/default': environment differs from the baseline ({field}: '{baseline}' vs '{current}')"));
  }

  [Then("^no relative change is printed for that benchmark$")]
  public void NoChange() {
    var rows = log.Last.Stdout.Split('\n').Where(line => line.StartsWith($"| {Id} |", StringComparison.Ordinal)).ToList();
    Assert.That(rows, Has.Count.EqualTo(1));
    Assert.That(rows[0], Does.Not.Contain("%").And.Contain("not compared"));
  }

  [Then("^no result file is written for " + StepText.Quoted + "$")]
  public void NoResult(string id) => Assert.That(Directory.Exists(workspace.Resolve($"artifacts/bench/{id}")) && Directory.EnumerateFiles(workspace.Resolve($"artifacts/bench/{id}")).Any(), Is.False);

  [Then(@"^diagnostic PLY0051 is reported and the tooling exit code is (\d+)$")]
  public void MismatchReported(int exitCode) {
    Assert.That(log.Last.ExitCode, Is.EqualTo(exitCode), log.Last.Output);
    Assert.That(log.Last.Stderr, Does.Contain($"error PLY0051: '{Id}/default' vector 'stdout': expected 30, actual 343939393939353030303030, first difference at byte 0"));
  }

}
