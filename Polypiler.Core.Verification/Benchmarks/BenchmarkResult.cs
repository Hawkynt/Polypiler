using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Polypiler.Core.Verification.Benchmarks;

/// <summary>One measured run.</summary>
/// <param name="WallNs">The wall-clock time in nanoseconds; for a <c>dotnet</c> benchmark the time of one operation.</param>
/// <param name="CpuNs">The processor time of the run's process tree in nanoseconds; <see langword="null"/> where it is not measured.</param>
/// <param name="PeakRssBytes">The largest resident memory of the run's process tree; <see langword="null"/> where it is not measured.</param>
public sealed record BenchmarkSample(long WallNs, long? CpuNs, long? PeakRssBytes);

/// <summary>
/// The result of one benchmark variant, <c>artifacts/bench/&lt;id&gt;/&lt;variant&gt;.json</c> (schema
/// <c>spec/schemas/bench-result.schema.json</c>): the samples of the measured runs, their statistics,
/// the sizes and the environment they were measured in. Written canonically, so that equal results
/// are equal files.
/// </summary>
/// <param name="Id">The benchmark.</param>
/// <param name="Variant">The variant.</param>
/// <param name="Commit">The commit measured.</param>
/// <param name="Environment">The machine and toolchains.</param>
/// <param name="Samples">The measured runs, warm-up runs excluded.</param>
/// <param name="BuildNs">The median time of the build steps in nanoseconds; <see langword="null"/> without a build.</param>
/// <param name="SourceBytes">The bytes of the source files the definition lists; <see langword="null"/> when it lists none.</param>
/// <param name="BinaryBytes">The bytes of the build's output the definition lists; <see langword="null"/> when it lists none.</param>
/// <param name="AllocatedBytes">The bytes allocated per operation (<c>dotnet</c> only).</param>
/// <param name="Allocations">The allocations per operation (<c>dotnet</c> only, where the measurement provides them).</param>
/// <param name="Extra">Further named metrics the caller supplied, for example instruction counts.</param>
public sealed record BenchmarkResult(
  string Id,
  string Variant,
  string Commit,
  BenchmarkEnvironment Environment,
  IReadOnlyList<BenchmarkSample> Samples,
  long? BuildNs,
  long? SourceBytes,
  long? BinaryBytes,
  long? AllocatedBytes,
  long? Allocations,
  IReadOnlyDictionary<string, long> Extra
) {

  /// <summary>The version of the result format.</summary>
  public const int SchemaVersion = 1;

  /// <summary>The metric of the wall-clock time.</summary>
  public const string WallNsMetric = "wallNs";

  /// <summary>The metric of the processor time.</summary>
  public const string CpuNsMetric = "cpuNs";

  /// <summary>The metric of the peak resident memory.</summary>
  public const string PeakRssBytesMetric = "peakRssBytes";

  /// <summary>
  /// The metrics every result can carry, PRD §13.2: execution time (wall and CPU), peak memory, build
  /// time, generated source size, binary size and allocations. <see cref="Extra"/> adds named ones.
  /// </summary>
  public static IReadOnlyList<string> Metrics { get; } = [WallNsMetric, CpuNsMetric, PeakRssBytesMetric, "buildNs", "sourceBytes", "binaryBytes", "allocatedBytes", "allocations"];

  /// <summary>The further metrics in ordinal order of their names.</summary>
  /// <exception cref="ArgumentNullException">The map is <see langword="null"/>.</exception>
  public IReadOnlyDictionary<string, long> Extra {
    get;
    init => field = Sorted(value);
  } = Sorted(Extra);

  private static SortedDictionary<string, long> Sorted(IReadOnlyDictionary<string, long> extra)
    => new(extra?.ToDictionary() ?? throw new ArgumentNullException(nameof(Extra)), StringComparer.Ordinal);

  /// <summary>The statistics of each sample metric measured in every sample, in the order wall time, CPU time, peak memory.</summary>
  public IReadOnlyDictionary<string, MetricStatistics> Stats {
    get {
      var stats = new Dictionary<string, MetricStatistics>(StringComparer.Ordinal);
      if (this.Samples.Count == 0)
        return stats;

      stats[WallNsMetric] = MetricStatistics.Of([.. this.Samples.Select(sample => sample.WallNs)]);
      if (this.Samples.All(sample => sample.CpuNs.HasValue))
        stats[CpuNsMetric] = MetricStatistics.Of([.. this.Samples.Select(sample => sample.CpuNs!.Value)]);
      if (this.Samples.All(sample => sample.PeakRssBytes.HasValue))
        stats[PeakRssBytesMetric] = MetricStatistics.Of([.. this.Samples.Select(sample => sample.PeakRssBytes!.Value)]);
      return stats;
    }
  }

  /// <summary>
  /// The value a comparison uses for a metric: the median of a sample metric, the value of a single
  /// one, or of an extra one; <see langword="null"/> when the result does not carry it.
  /// </summary>
  /// <param name="metric">The metric's name.</param>
  public long? Value(string metric) => metric switch {
    WallNsMetric or CpuNsMetric or PeakRssBytesMetric => this.Stats.TryGetValue(metric, out var stats) ? stats.Median : null,
    "buildNs" => this.BuildNs,
    "sourceBytes" => this.SourceBytes,
    "binaryBytes" => this.BinaryBytes,
    "allocatedBytes" => this.AllocatedBytes,
    "allocations" => this.Allocations,
    _ => this.Extra.TryGetValue(metric, out var value) ? value : null
  };

  /// <summary>The metrics this result carries a value for, the standard ones first, then the extra ones.</summary>
  public IEnumerable<string> PresentMetrics => Metrics.Concat(this.Extra.Keys).Where(metric => this.Value(metric).HasValue);

  /// <summary>The canonical JSON: two-space indentation, LF line ends, fixed property order, maps in ordinal key order, a final line end.</summary>
  public string ToJson() {
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" })) {
      writer.WriteStartObject();
      writer.WriteNumber("schemaVersion", SchemaVersion);
      writer.WriteString("id", this.Id);
      writer.WriteString("variant", this.Variant);
      writer.WriteString("commit", this.Commit);
      writer.WritePropertyName("environment");
      this.Environment.Write(writer, true);
      writer.WriteStartArray("samples");
      foreach (var sample in this.Samples) {
        writer.WriteStartObject();
        writer.WriteNumber("wallNs", sample.WallNs);
        WriteNumber(writer, "cpuNs", sample.CpuNs);
        WriteNumber(writer, "peakRssBytes", sample.PeakRssBytes);
        writer.WriteEndObject();
      }

      writer.WriteEndArray();
      writer.WriteStartObject("stats");
      foreach (var (metric, stats) in this.Stats) {
        writer.WriteStartObject(metric);
        writer.WriteNumber("median", stats.Median);
        writer.WriteNumber("mad", stats.Mad);
        writer.WriteNumber("min", stats.Min);
        writer.WriteNumber("max", stats.Max);
        writer.WriteEndObject();
      }

      writer.WriteEndObject();
      WriteNumber(writer, "buildNs", this.BuildNs);
      WriteNumber(writer, "sourceBytes", this.SourceBytes);
      WriteNumber(writer, "binaryBytes", this.BinaryBytes);
      WriteNumber(writer, "allocatedBytes", this.AllocatedBytes);
      WriteNumber(writer, "allocations", this.Allocations);
      writer.WriteStartObject("extra");
      foreach (var (name, value) in this.Extra)
        writer.WriteNumber(name, value);
      writer.WriteEndObject();
      writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
  }

  private static void WriteNumber(Utf8JsonWriter writer, string name, long? value) {
    if (value is { } number)
      writer.WriteNumber(name, number);
    else
      writer.WriteNull(name);
  }

  /// <summary>Reads a result file.</summary>
  /// <param name="path">The file.</param>
  /// <exception cref="BenchmarkFileException">The file cannot be read or is not a result of schema 1.</exception>
  public static BenchmarkResult Load(string path) {
    string text;
    try {
      text = File.ReadAllText(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      throw new BenchmarkFileException(path, 0, 0, $"cannot be read: {exception.Message}");
    }

    return Parse(text, path);
  }

  /// <summary>Reads the text of a result file. The statistics are not read: they follow from the samples.</summary>
  /// <param name="text">The text.</param>
  /// <param name="file">The file's name, for the messages.</param>
  /// <exception cref="BenchmarkFileException">The text is not a result of schema 1.</exception>
  public static BenchmarkResult Parse(string text, string file) {
    JsonDocument document;
    try {
      document = JsonDocument.Parse(text);
    } catch (JsonException exception) {
      throw new BenchmarkFileException(file, (int)(exception.LineNumber ?? 0) + 1, (int)(exception.BytePositionInLine ?? 0) + 1, $"not valid JSON: {exception.Message}");
    }

    using (document) {
      var reader = new JsonFields(file);
      var root = document.RootElement;
      if (reader.Integer(root, "schemaVersion") != SchemaVersion)
        throw reader.Error($"'schemaVersion' must be {SchemaVersion}");

      var environment = reader.Object(root, "environment");
      var toolchains = reader.Object(environment, "toolchains").EnumerateObject().ToDictionary(property => property.Name, property => reader.TextOf(property.Value, $"environment.toolchains.{property.Name}"), StringComparer.Ordinal);
      return new(
        reader.Text(root, "id"),
        reader.Text(root, "variant"),
        reader.Text(root, "commit"),
        new(reader.Text(environment, "os"), reader.Text(environment, "osVersion"), reader.Text(environment, "architecture"), reader.Text(environment, "cpuModel"),
          (int)reader.Integer(environment, "logicalCores"), toolchains),
        [.. reader.Array(root, "samples").Select(sample => new BenchmarkSample(reader.Integer(sample, "wallNs"), reader.OptionalInteger(sample, "cpuNs"), reader.OptionalInteger(sample, "peakRssBytes")))],
        reader.OptionalInteger(root, "buildNs"),
        reader.OptionalInteger(root, "sourceBytes"),
        reader.OptionalInteger(root, "binaryBytes"),
        reader.OptionalInteger(root, "allocatedBytes"),
        reader.OptionalInteger(root, "allocations"),
        reader.Object(root, "extra").EnumerateObject().ToDictionary(property => property.Name, property => reader.IntegerOf(property.Value, $"extra.{property.Name}"), StringComparer.Ordinal));
    }
  }

  /// <summary>Reads the fields of a result, naming the one that is missing or of the wrong kind.</summary>
  private sealed class JsonFields(string file) {

    public BenchmarkFileException Error(string reason) => new(file, 0, 0, reason);

    private JsonElement Property(JsonElement owner, string name)
      => owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) ? value : throw this.Error($"missing property '{name}'");

    public JsonElement Object(JsonElement owner, string name)
      => this.Property(owner, name) is { ValueKind: JsonValueKind.Object } value ? value : throw this.Error($"'{name}' must be an object");

    public IEnumerable<JsonElement> Array(JsonElement owner, string name)
      => this.Property(owner, name) is { ValueKind: JsonValueKind.Array } value ? [.. value.EnumerateArray()] : throw this.Error($"'{name}' must be an array");

    public string Text(JsonElement owner, string name) => this.TextOf(this.Property(owner, name), name);

    public string TextOf(JsonElement value, string name)
      => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw this.Error($"'{name}' must be text");

    public long Integer(JsonElement owner, string name) => this.IntegerOf(this.Property(owner, name), name);

    public long IntegerOf(JsonElement value, string name)
      => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : throw this.Error($"'{name}' must be an integer");

    public long? OptionalInteger(JsonElement owner, string name) {
      var value = this.Property(owner, name);
      return value.ValueKind == JsonValueKind.Null ? null : this.IntegerOf(value, name);
    }

  }

}
