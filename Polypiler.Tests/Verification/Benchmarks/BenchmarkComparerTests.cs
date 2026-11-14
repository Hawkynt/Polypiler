using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Benchmarks;
using static Polypiler.Tests.Verification.Benchmarks.BenchTesting;

namespace Polypiler.Tests.Verification.Benchmarks;

/// <summary>The comparison of two result sets and the budget file (test design 6 to 15 and 19).</summary>
[TestFixture]
public sealed class BenchmarkComparerTests {

  private readonly DiagnosticBag _bag = new();

  private static BudgetFile Budget(decimal percent, string metric = "wallNs", decimal? maxRelativeMad = null)
    => new("budgets-test.yaml", maxRelativeMad, [new("loop", "default", metric, percent)]);

  private ComparisonRow Row(BenchmarkResult baseline, BenchmarkResult current, BudgetFile? budgets, string metric = "wallNs")
    => BenchmarkComparer.Compare([baseline], [current], budgets, this._bag).Rows.Single(row => row.Metric == metric);

  private static BenchmarkResult Loop(params long[] wallMs) => Result("loop", "default", null, wallMs);

  /// <summary>Test design 6 and the Gherkin outline: 110 against 100 at 10 % passes, 111 does not.</summary>
  [TestCase(90, "-10 %", "within budget")]
  [TestCase(110, "+10 %", "within budget")]
  [TestCase(111, "+11 %", "regressed")]
  public void ABudget_GatesARegression_EqualityPasses(long current, string change, string note) {
    var row = this.Row(Loop(100), Loop(current), Budget(10));
    Assert.That((row.Change, row.Note), Is.EqualTo((change, note)));
    Assert.That(this._bag.Items.Select(diagnostic => diagnostic.Code), Is.EqualTo(note == "regressed" ? new[] { "PLY0073" } : []));
  }

  [Test]
  public void ARegression_NamesMetricPercentValuesAndBudgetFile() {
    this.Row(Loop(100), Loop(111), Budget(10));
    var diagnostic = this._bag.Items.Single();
    Assert.That(diagnostic.Message, Is.EqualTo("benchmark 'loop/default': wallNs regressed by 11 % (baseline 100000000, current 111000000), budget 10 % from budgets-test.yaml"));
    Assert.That((diagnostic.ExitCode, this._bag.ExitCode), Is.EqualTo((ExitCode.EnvironmentError, ExitCode.EnvironmentError)));
  }

  /// <summary>Test design 7: a budget of 0 % lets an equal value pass and fails one nanosecond more.</summary>
  [Test]
  public void AZeroBudget_PassesEquality_AndFailsOneNanosecondMore() {
    Assert.That(Budget(0).Budgets[0].IsExceeded(100, 100), Is.False);
    Assert.That(Budget(0).Budgets[0].IsExceeded(100, 101), Is.True);
    var slower = Loop(100) with { Samples = [new(100_000_001, 1, 1)] };
    Assert.That(this.Row(Loop(100), slower, Budget(0)).Note, Is.EqualTo("regressed"));
  }

  /// <summary>Test design 8: a baseline of zero is never divided by.</summary>
  [TestCase(0L, "0 %", "record only")]
  [TestCase(1L, "", "new value")]
  public void AZeroBaseline_IsNotDividedBy(long current, string change, string note) {
    var row = this.Row(Loop(1) with { BinaryBytes = 0 }, Loop(1) with { BinaryBytes = current }, null, "binaryBytes");
    Assert.That((row.Change, row.Note), Is.EqualTo((change, note)));
  }

  /// <summary>Test design 9: the relative MAD on the limit is compared, above it is unstable.</summary>
  [TestCase(new long[] { 90, 100, 110 }, false)]
  [TestCase(new long[] { 89, 100, 111 }, true)]
  public void TheRelativeMad_OnTheLimitIsCompared_AboveItIsUnstable(long[] samples, bool unstable) {
    var rows = BenchmarkComparer.Compare([Loop(100)], [Loop(samples)], Budget(10, maxRelativeMad: 0.1m), this._bag).Rows;
    Assert.That(rows.Any(row => row.Note == "not compared: unstable"), Is.EqualTo(unstable));
    Assert.That(this._bag.Items.Select(diagnostic => diagnostic.Message), Is.EqualTo(unstable ? new[] { "benchmark 'loop/default': unstable, relative MAD 0.11 exceeds 0.1" } : []));
  }

  [Test]
  public void AnUnstableBaseline_IsNotComparedEither() {
    BenchmarkComparer.Compare([Loop(50, 100, 150)], [Loop(100)], Budget(10, maxRelativeMad: 0.1m), this._bag);
    Assert.That(this._bag.Items.Single().Code, Is.EqualTo("PLY0072"));
  }

  [Test]
  public void AZeroMedian_IsNeverUnstable()
    => Assert.That(BenchmarkComparer.Compare([Loop(0)], [Loop(0)], Budget(10, maxRelativeMad: 0.1m), this._bag).Rows, Has.Some.Matches<ComparisonRow>(row => row.Metric == "wallNs"));

  /// <summary>Test design 12: improvement, unchanged and regression without a budget are all recorded only.</summary>
  [TestCase(90, "-10 %")]
  [TestCase(100, "0 %")]
  [TestCase(500, "+400 %")]
  public void WithoutABudget_EveryChangeIsRecordedOnly(long current, string change) {
    var row = this.Row(Loop(100), Loop(current), null);
    Assert.That((row.Change, row.Note), Is.EqualTo((change, ComparisonTable.RecordOnly)));
    Assert.That(this._bag.Items, Is.Empty);
  }

  [Test]
  public void ABudgetOfAnotherMetric_LeavesThisOneRecordedOnly()
    => Assert.That(this.Row(Loop(100), Loop(500), Budget(10, "cpuNs"), "wallNs").Note, Is.EqualTo(ComparisonTable.RecordOnly));

  /// <summary>Test design 10: every metric is compared.</summary>
  [Test]
  public void EveryMetric_IsCompared() {
    var full = Loop(100) with { BuildNs = 5, BinaryBytes = 9, AllocatedBytes = 7, Allocations = 2 };
    var rows = BenchmarkComparer.Compare([full], [full], null, this._bag).Rows;
    Assert.That(rows.Select(row => row.Metric), Is.EqualTo(new[] { "wallNs", "cpuNs", "peakRssBytes", "buildNs", "sourceBytes", "binaryBytes", "allocatedBytes", "allocations" }));
    Assert.That(rows.Select(row => row.Change).Distinct(), Is.EqualTo(new[] { "0 %" }));
  }

  /// <summary>Test design 14: an extra metric is compared and budgeted like any other.</summary>
  [Test]
  public void AnExtraMetric_IsComparedLikeAnyOther() {
    var before = Loop(100) with { Extra = new Dictionary<string, long> { ["instructions"] = 42 } };
    var after = Loop(100) with { Extra = new Dictionary<string, long> { ["instructions"] = 50 } };
    var row = this.Row(before, after, Budget(10, "instructions"), "instructions");
    Assert.That((row.Baseline, row.Current, row.Change, row.Note), Is.EqualTo(((long?)42, (long?)50, "+19.05 %", "regressed")));
  }

  [Test]
  public void AMetricOnOneSideOnly_IsNewOrRemoved() {
    var rows = BenchmarkComparer.Compare([Loop(100) with { BuildNs = 1 }], [Loop(100) with { BinaryBytes = 1 }], null, this._bag).Rows;
    Assert.That(rows.Single(row => row.Metric == "buildNs").Note, Is.EqualTo("removed"));
    Assert.That(rows.Single(row => row.Metric == "binaryBytes").Note, Is.EqualTo("new"));
  }

  /// <summary>Test design 13: a benchmark only in the baseline or only in the current set is listed, never a regression.</summary>
  [Test]
  public void ABenchmarkOnOneSideOnly_IsRemovedOrNew() {
    var rows = BenchmarkComparer.Compare([Result("gone", "default", null, 1)], [Result("fresh", "default", null, 1)], Budget(0), this._bag).Rows;
    Assert.That(rows.Select(row => (row.Id, row.Note)), Is.EqualTo(new[] { ("fresh", "new"), ("gone", "removed") }));
    Assert.That(this._bag.Items, Is.Empty);
  }

  /// <summary>Test design 15: a different environment is not compared, and the first differing field is named.</summary>
  [TestCase(4, "24.22.2", "environment differs from the baseline (logicalCores: '8' vs '4')")]
  [TestCase(8, "22.23.3", "environment differs from the baseline (toolchains.node: '24.22.2' vs '22.23.3')")]
  public void ADifferentEnvironment_IsNotCompared(int cores, string node, string reason) {
    var rows = BenchmarkComparer.Compare([Loop(100)], [Result("loop", "default", Machine(cores, node), 500)], null, this._bag).Rows;
    Assert.That(rows.Single().Change, Is.Empty);
    Assert.That(rows.Single().Note, Does.StartWith("not compared: environment differs"));
    var diagnostic = this._bag.Items.Single();
    Assert.That((diagnostic.Code, diagnostic.Message), Is.EqualTo(("PLY0072", $"benchmark 'loop/default': {reason}")));
  }

  [Test]
  public void TheFixtureDirectories_AreComparedFileByFile() {
    var results = Path.Combine(FixtureFolder, "results");
    var table = BenchmarkComparer.Compare(Path.Combine(results, "baseline"), Path.Combine(results, "current"), BudgetFile.Load(Path.Combine(FixtureFolder, "budgets-test.yaml")), this._bag);
    Assert.That(this._bag.Items, Is.Empty);
    Assert.That(table.Rows.Select(row => (row.Id, row.Metric, row.Change, row.Note)), Is.EqualTo(new[] {
      ("new-one", "", "", "new"),
      ("removed-one", "", "", "removed"),
      ("sum-loop", "wallNs", "+10 %", "within budget"),
      ("sum-loop", "cpuNs", "+10 %", ComparisonTable.RecordOnly),
      ("sum-loop", "peakRssBytes", "0 %", ComparisonTable.RecordOnly),
      ("sum-loop", "sourceBytes", "0 %", ComparisonTable.RecordOnly)
    }));
    Assert.That(table.ToMarkdown(), Does.Contain("| sum-loop | default | wallNs | 100000000 | 110000000 | +10 % | within budget |\n"));
  }

  [Test]
  public void AnUnreadableResultFile_IsReported_AndAMissingDirectoryIsEmpty() {
    using var scratch = new Polypiler.Tests.Tooling.Toolchains.ScratchFolder();
    File.WriteAllText(scratch.File("broken/default.json"), "{");
    var table = BenchmarkComparer.Compare(scratch.Path, Path.Combine(scratch.Path, "missing"), null, this._bag);
    Assert.That((table.Rows.Count, this._bag.Items.Single().Code), Is.EqualTo((0, "PLY0078")));
    Assert.That(() => BenchmarkComparer.Compare(scratch.Path, scratch.Path, null, null!), Throws.ArgumentNullException);
    Assert.That(() => BenchmarkComparer.Compare([], [], null, null!), Throws.ArgumentNullException);
  }

  [Test]
  public void TwoResultsOfTheSameVariant_CountOnce()
    => Assert.That(BenchmarkComparer.Compare([Loop(100), Loop(1)], [Loop(100)], null, this._bag).Rows.Single(row => row.Metric == "wallNs").Change, Is.EqualTo("0 %"));

}
