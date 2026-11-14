using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Verification.Benchmarks;

/// <summary>One line of a comparison: a metric of a benchmark variant, or the reason the variant was not compared.</summary>
/// <param name="Id">The benchmark.</param>
/// <param name="Variant">The variant.</param>
/// <param name="Metric">The metric; empty for a line about the whole variant.</param>
/// <param name="Baseline">The baseline's value; <see langword="null"/> when it has none.</param>
/// <param name="Current">The current value; <see langword="null"/> when it has none.</param>
/// <param name="Change">The relative change, for example <c>+10 %</c>; empty when none is computed.</param>
/// <param name="Note">How the line is judged: <c>record only</c>, <c>within budget</c>, <c>regressed</c>, <c>new</c>, <c>removed</c>, <c>new value</c> or <c>not compared</c> with the reason.</param>
public sealed record ComparisonRow(string Id, string Variant, string Metric, long? Baseline, long? Current, string Change, string Note);

/// <summary>The comparison of two result sets, line by line.</summary>
/// <param name="Rows">The lines, by benchmark, variant and metric.</param>
public sealed record ComparisonTable(IReadOnlyList<ComparisonRow> Rows) {

  /// <summary>The note of a metric without a budget.</summary>
  public const string RecordOnly = "record only";

  /// <summary>The table as Markdown.</summary>
  public string ToMarkdown() {
    var text = new StringBuilder();
    text.Append("| Benchmark | Variant | Metric | Baseline | Current | Change | Note |\n");
    text.Append("| --- | --- | --- | ---: | ---: | ---: | --- |\n");
    foreach (var row in this.Rows)
      text.Append(CultureInfo.InvariantCulture, $"| {row.Id} | {row.Variant} | {row.Metric} | {Cell(row.Baseline)} | {Cell(row.Current)} | {row.Change} | {row.Note} |\n");
    return text.ToString();
  }

  private static string Cell(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

}

/// <summary>
/// Compares a current result set with a baseline. Results are paired by ID and variant; a pair from
/// different environments, or with an unstable wall time when the budget file limits the dispersion,
/// is not compared (<c>PLY0072</c>). A metric with a budget that regresses beyond it is <c>PLY0073</c>;
/// every other metric is recorded with its relative change. A benchmark on one side only is
/// <c>new</c> or <c>removed</c>, never a regression.
/// </summary>
public static class BenchmarkComparer {

  /// <summary>Compares the result files of two directories, <c>&lt;dir&gt;/&lt;id&gt;/&lt;variant&gt;.json</c>.</summary>
  /// <param name="baselineDir">The baseline's results.</param>
  /// <param name="currentDir">The current results.</param>
  /// <param name="budgets">The budgets; <see langword="null"/> to record every metric.</param>
  /// <param name="sink">Receives <c>PLY0072</c>, <c>PLY0073</c>, and <c>PLY0078</c> for a result file that cannot be read.</param>
  public static ComparisonTable Compare(string baselineDir, string currentDir, BudgetFile? budgets, IDiagnosticSink sink) {
    ArgumentNullException.ThrowIfNull(sink);
    return Compare(Load(baselineDir, sink), Load(currentDir, sink), budgets, sink);
  }

  /// <summary>Compares two result sets; of two results with the same ID and variant the first counts.</summary>
  /// <param name="baseline">The baseline's results.</param>
  /// <param name="current">The current results.</param>
  /// <param name="budgets">The budgets; <see langword="null"/> to record every metric.</param>
  /// <param name="sink">Receives <c>PLY0072</c> and <c>PLY0073</c>.</param>
  public static ComparisonTable Compare(IEnumerable<BenchmarkResult> baseline, IEnumerable<BenchmarkResult> current, BudgetFile? budgets, IDiagnosticSink sink) {
    ArgumentNullException.ThrowIfNull(sink);
    var before = baseline.DistinctBy(result => (result.Id, result.Variant)).ToDictionary(result => (result.Id, result.Variant));
    var after = current.DistinctBy(result => (result.Id, result.Variant)).ToDictionary(result => (result.Id, result.Variant));
    var rows = new List<ComparisonRow>();
    foreach (var key in before.Keys.Union(after.Keys).OrderBy(key => key.Id, StringComparer.Ordinal).ThenBy(key => key.Variant, StringComparer.Ordinal)) {
      if (!after.TryGetValue(key, out var now))
        rows.Add(new(key.Id, key.Variant, "", null, null, "", "removed"));
      else if (!before.TryGetValue(key, out var then))
        rows.Add(new(key.Id, key.Variant, "", null, null, "", "new"));
      else
        rows.AddRange(ComparePair(then, now, budgets, sink));
    }

    return new(rows);
  }

  private static IEnumerable<ComparisonRow> ComparePair(BenchmarkResult baseline, BenchmarkResult current, BudgetFile? budgets, IDiagnosticSink sink) {
    var (id, variant) = (current.Id, current.Variant);
    if (baseline.Environment.FirstDifference(current.Environment) is var (field, mine, theirs)) {
      sink.Report(Diagnostic.Create(BenchmarkDiagnostics.NotComparable, null, id, variant, $"environment differs from the baseline ({field}: '{mine}' vs '{theirs}')"));
      return [new(id, variant, "", null, null, "", $"not compared: environment differs ({field})")];
    }

    if (budgets?.MaxRelativeMad is { } limit && (Unstable(current, limit) ?? Unstable(baseline, limit)) is { } relative) {
      sink.Report(Diagnostic.Create(BenchmarkDiagnostics.NotComparable, null, id, variant, $"unstable, relative MAD {Format(relative)} exceeds {Format(limit)}"));
      return [new(id, variant, "", null, null, "", "not compared: unstable")];
    }

    var rows = new List<ComparisonRow>();
    foreach (var metric in baseline.PresentMetrics.Union(current.PresentMetrics)) {
      var (before, after) = (baseline.Value(metric), current.Value(metric));
      if (before is not { } b)
        rows.Add(new(id, variant, metric, null, after, "", "new"));
      else if (after is not { } c)
        rows.Add(new(id, variant, metric, b, null, "", "removed"));
      else if (b == 0)
        rows.Add(new(id, variant, metric, b, c, c == 0 ? "0 %" : "", c == 0 ? Judge(budgets, id, variant, metric, b, c, sink) : "new value"));
      else
        rows.Add(new(id, variant, metric, b, c, Change(b, c), Judge(budgets, id, variant, metric, b, c, sink)));
    }

    return rows;
  }

  /// <summary>The note of a metric present on both sides, reporting <c>PLY0073</c> for a regression beyond its budget.</summary>
  private static string Judge(BudgetFile? budgets, string id, string variant, string metric, long baseline, long current, IDiagnosticSink sink) {
    if (budgets?.Find(id, variant, metric) is not { } budget)
      return ComparisonTable.RecordOnly;
    if (!budget.IsExceeded(baseline, current))
      return "within budget";

    var regression = (current - baseline) * 100m / baseline;
    sink.Report(Diagnostic.Create(BenchmarkDiagnostics.BudgetExceeded, null, id, variant, metric, Format(regression), baseline, current, Format(budget.MaxRegressionPercent), budgets.File));
    return "regressed";
  }

  /// <summary>The relative MAD of a result's wall time when it is above the limit; <see langword="null"/> when it is not or cannot be computed.</summary>
  private static decimal? Unstable(BenchmarkResult result, decimal limit)
    => result.Stats.TryGetValue(BenchmarkResult.WallNsMetric, out var stats) && stats.Median != 0 && (decimal)stats.Mad / stats.Median is var relative && relative > limit ? relative : null;

  /// <summary>The relative change from a non-zero baseline, signed, to at most two decimals: <c>+400 %</c>, <c>-10 %</c>, <c>0 %</c>.</summary>
  public static string Change(long baseline, long current) {
    var percent = (current - (decimal)baseline) * 100m / baseline;
    return (percent > 0 ? "+" : "") + Format(percent) + " %";
  }

  private static string Format(decimal value) => Math.Round(value, 2, MidpointRounding.ToEven).ToString("0.##", CultureInfo.InvariantCulture);

  /// <summary>Every result file below a directory, <c>&lt;id&gt;/&lt;variant&gt;.json</c>; a missing directory has none.</summary>
  private static List<BenchmarkResult> Load(string directory, IDiagnosticSink sink) {
    var results = new List<BenchmarkResult>();
    if (!Directory.Exists(directory))
      return results;

    foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
      try {
        results.Add(BenchmarkResult.Load(file));
      } catch (BenchmarkFileException exception) {
        sink.Report(exception.ToDiagnostic());
      }
    }

    return results;
  }

}
