using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Verification.Benchmarks;

/// <summary>
/// <c>artifacts/bench/summary.md</c>: one table row per benchmark and variant of a run. A run records,
/// it never judges: every row carries the note <c>record only</c>; budgets apply in a comparison.
/// </summary>
public static class BenchmarkSummary {

  /// <summary>The file name of the summary in the output directory.</summary>
  public const string FileName = "summary.md";

  /// <summary>The summary of results as Markdown, ordered by ID and variant.</summary>
  /// <param name="results">The results.</param>
  public static string ToMarkdown(IEnumerable<BenchmarkResult> results) {
    var text = new StringBuilder();
    text.Append("| Benchmark | Variant | Runs | Wall median (ns) | Wall MAD (ns) | CPU median (ns) | Peak RSS (bytes) | Build (ns) | Source bytes | Binary bytes | Allocated bytes | Note |\n");
    text.Append("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |\n");
    foreach (var result in results.OrderBy(result => result.Id, StringComparer.Ordinal).ThenBy(result => result.Variant, StringComparer.Ordinal)) {
      var stats = result.Stats;
      var wall = stats.GetValueOrDefault(BenchmarkResult.WallNsMetric);
      text.Append(CultureInfo.InvariantCulture, $"| {result.Id} | {result.Variant} | {result.Samples.Count} | {Cell(wall?.Median)} | {Cell(wall?.Mad)} | ");
      text.Append(CultureInfo.InvariantCulture, $"{Cell(stats.GetValueOrDefault(BenchmarkResult.CpuNsMetric)?.Median)} | {Cell(stats.GetValueOrDefault(BenchmarkResult.PeakRssBytesMetric)?.Median)} | ");
      text.Append(CultureInfo.InvariantCulture, $"{Cell(result.BuildNs)} | {Cell(result.SourceBytes)} | {Cell(result.BinaryBytes)} | {Cell(result.AllocatedBytes)} | {ComparisonTable.RecordOnly} |\n");
    }

    return text.ToString();
  }

  private static string Cell(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

}
