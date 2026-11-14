using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Polypiler.Core.Verification.Benchmarks;

/// <summary>What a BenchmarkDotNet run contributes to a result: one sample per measured iteration and the allocations.</summary>
/// <param name="Samples">The time of one operation in each measured iteration, in nanoseconds.</param>
/// <param name="AllocatedBytes">The bytes allocated per operation; <see langword="null"/> without the memory diagnoser.</param>
public sealed record DotNetMeasurement(IReadOnlyList<BenchmarkSample> Samples, long? AllocatedBytes);

/// <summary>
/// Reads the full JSON export of BenchmarkDotNet (<c>*-report-full.json</c> under
/// <c>BenchmarkDotNet.Artifacts/results</c>), the form in which a <c>dotnet</c> benchmark reports.
/// </summary>
public static class BenchmarkDotNetExport {

  /// <summary>The folder of the run's directory BenchmarkDotNet writes its exports to, with <c>--artifacts BenchmarkDotNet.Artifacts</c>.</summary>
  public const string ResultsFolder = "BenchmarkDotNet.Artifacts/results";

  /// <summary>The ending of the full JSON export's file name.</summary>
  public const string FullJsonSuffix = "-report-full.json";

  /// <summary>Finds the first full JSON export, in ordinal order of the names, among the files a run collected.</summary>
  /// <param name="collected">The collected files by name.</param>
  /// <returns>The export's text; <see langword="null"/> when there is none.</returns>
  public static string? Find(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> collected)
    => collected.Keys.Where(name => name.EndsWith(FullJsonSuffix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)
      .Select(name => System.Text.Encoding.UTF8.GetString(collected[name].Span)).FirstOrDefault();

  /// <summary>Converts the first benchmark of an export.</summary>
  /// <param name="json">The export's text.</param>
  /// <param name="file">The export's name, for the messages.</param>
  /// <exception cref="BenchmarkFileException">The text is not an export with a benchmark and its measured values.</exception>
  public static DotNetMeasurement Convert(string json, string file) {
    JsonDocument document;
    try {
      document = JsonDocument.Parse(json);
    } catch (JsonException exception) {
      throw new BenchmarkFileException(file, 0, 0, $"not valid JSON: {exception.Message}");
    }

    using (document) {
      if (!document.RootElement.TryGetProperty("Benchmarks", out var benchmarks) || benchmarks.ValueKind != JsonValueKind.Array || benchmarks.GetArrayLength() == 0)
        throw new BenchmarkFileException(file, 0, 0, "'Benchmarks' must be a non-empty array");

      var benchmark = benchmarks[0];
      if (!benchmark.TryGetProperty("Statistics", out var statistics) || statistics.ValueKind != JsonValueKind.Object
          || !statistics.TryGetProperty("OriginalValues", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0)
        throw new BenchmarkFileException(file, 0, 0, "'Benchmarks[0].Statistics.OriginalValues' must be a non-empty array");

      var samples = values.EnumerateArray()
        .Select(value => value.ValueKind == JsonValueKind.Number ? new BenchmarkSample(Nanoseconds(value.GetDouble()), null, null) : throw new BenchmarkFileException(file, 0, 0, "'OriginalValues' must hold numbers"))
        .ToArray();
      long? allocated = benchmark.TryGetProperty("Memory", out var memory) && memory.ValueKind == JsonValueKind.Object
        && memory.TryGetProperty("BytesAllocatedPerOperation", out var bytes) && bytes.ValueKind == JsonValueKind.Number && bytes.TryGetInt64(out var count)
        ? count
        : null;
      return new(samples, allocated);
    }
  }

  /// <summary>A time in nanoseconds as an integer, a half rounded to the even neighbour.</summary>
  private static long Nanoseconds(double value) => (long)Math.Round(value, MidpointRounding.ToEven);

}
