using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Benchmarks;

/// <summary>
/// What a benchmark run needs from the machine: the repository the paths are relative to, the commit
/// measured, the version of a toolchain, and how the environment is detected.
/// </summary>
/// <param name="RepositoryRoot">The directory the definition's paths are relative to.</param>
/// <param name="Commit">The commit measured.</param>
/// <param name="ToolchainVersion">The version of a toolchain by its key; <see langword="null"/> when it is not known.</param>
public sealed record BenchmarkHost(string RepositoryRoot, string Commit, Func<string, string?> ToolchainVersion) {

  /// <summary>The environment of the toolchain versions given; <see cref="BenchmarkEnvironment.Detect"/> unless replaced.</summary>
  public Func<IReadOnlyDictionary<string, string>, BenchmarkEnvironment> Environment { get; init; } = BenchmarkEnvironment.Detect;

  /// <summary>
  /// The placeholders of the build and run arguments, each replaced by its value wherever it occurs:
  /// <c>{root}</c> is always the repository root.
  /// </summary>
  public IReadOnlyDictionary<string, string> Placeholders { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

  /// <summary>The limits of every step; <see cref="RunLimits.Default"/> unless given.</summary>
  public RunLimits Limits { get; init; } = RunLimits.Default;

}

/// <summary>
/// Measures a benchmark variant through the cross-language runner. A <c>process</c> benchmark runs
/// <see cref="BenchmarkDefinition.Warmup"/> + <see cref="BenchmarkDefinition.Runs"/> times and records
/// the measured runs; a <c>dotnet</c> benchmark runs once and records the iterations of its
/// BenchmarkDotNet export. The program is never altered: a run that does not complete, exits non-zero
/// or prints other than <see cref="BenchmarkDefinition.ExpectStdout"/> ends the measurement without
/// a result.
/// </summary>
/// <param name="runner">Runs each plan.</param>
/// <param name="host">The repository, commit and toolchain versions.</param>
/// <param name="sink">Receives why a variant has no result.</param>
public sealed class BenchmarkRunner(ICrossLanguageRunner runner, BenchmarkHost host, IDiagnosticSink sink) {

  /// <summary>The placeholder of the repository root in the build and run arguments.</summary>
  public const string RootPlaceholder = "{root}";

  /// <summary>Measures a variant.</summary>
  /// <param name="definition">The benchmark.</param>
  /// <param name="variant">The variant's name.</param>
  /// <param name="extra">Further named metrics to store with the result, for example instruction counts.</param>
  /// <returns>The result; <see langword="null"/> when the measurement failed, the reason reported to the sink.</returns>
  /// <exception cref="ArgumentException">The definition has no such variant.</exception>
  public BenchmarkResult? Run(BenchmarkDefinition definition, string variant, IReadOnlyDictionary<string, long>? extra = null) {
    ArgumentNullException.ThrowIfNull(definition);
    var chosen = definition.Variant(variant);
    try {
      return this.Measure(definition, chosen, extra ?? new Dictionary<string, long>());
    } catch (BenchmarkFileException exception) {
      sink.Report(exception.ToDiagnostic());
      return null;
    }
  }

  private BenchmarkResult? Measure(BenchmarkDefinition definition, BenchmarkVariant variant, IReadOnlyDictionary<string, long> extra) {
    var files = this.Files(definition.Sources);
    var stdin = definition.Stdin is { } input ? this.Read(input) : [];
    var build = definition.Build is { } arguments ? new[] { new ProcessSpec(definition.Toolchain, this.Expand(arguments)) { Environment = variant.Environment } } : [];
    var execute = new ProcessSpec(definition.Toolchain, this.Expand([.. definition.Run, .. variant.Arguments]), stdin) { Environment = variant.Environment };
    var collect = definition.Kind == BenchmarkKind.DotNet ? [.. definition.Sizes.Binary, BenchmarkDotNetExport.ResultsFolder] : definition.Sizes.Binary;
    var plan = new RunPlan(files, build, execute, host.Limits, TrustLevel.Trusted) { Collect = collect };
    var count = definition.Kind == BenchmarkKind.DotNet ? 1 : definition.Warmup + definition.Runs;
    var warmup = definition.Kind == BenchmarkKind.DotNet ? 0 : definition.Warmup;

    var measured = new List<RunResult>();
    for (var index = 0; index < count; ++index) {
      var result = runner.RunAsync(plan).GetAwaiter().GetResult();
      if (!this.IsMeasurement(definition, variant, result))
        return null;
      if (index >= warmup)
        measured.Add(result);
    }

    var last = measured[^1];
    IReadOnlyList<BenchmarkSample> samples;
    long? allocated = null;
    if (definition.Kind == BenchmarkKind.DotNet) {
      var export = BenchmarkDotNetExport.Find(last.Collected)
        ?? throw new BenchmarkFileException(definition.Id, 0, 0, $"benchmark '{definition.Id}/{variant.Name}': BenchmarkDotNet wrote no '*{BenchmarkDotNetExport.FullJsonSuffix}' under '{BenchmarkDotNetExport.ResultsFolder}'");
      var measurement = BenchmarkDotNetExport.Convert(export, BenchmarkDotNetExport.ResultsFolder);
      (samples, allocated) = (measurement.Samples, measurement.AllocatedBytes);
    } else {
      samples = [.. measured.Select(run => new BenchmarkSample(Nanoseconds(run.WallTime), Nanoseconds(run.CpuTime), run.PeakMemoryBytes))];
    }

    var environment = host.Environment(new Dictionary<string, string>(StringComparer.Ordinal) { [definition.Toolchain] = host.ToolchainVersion(definition.Toolchain) ?? "unknown" });
    return new(
      definition.Id,
      variant.Name,
      host.Commit,
      environment,
      samples,
      definition.Build is null ? null : Statistics.Median([.. measured.Select(run => Nanoseconds(run.BuildTime))]),
      definition.Sizes.Source.Count == 0 ? null : definition.Sizes.Source.Sum(this.Size),
      definition.Sizes.Binary.Count == 0 ? null : definition.Sizes.Binary.Sum(path => Collected(last, path)),
      allocated,
      null,
      extra);
  }

  /// <summary>Whether a run counts: it completed, exited with 0 and printed what the definition expects; reports why not.</summary>
  private bool IsMeasurement(BenchmarkDefinition definition, BenchmarkVariant variant, RunResult result) {
    if (result.Outcome != RunOutcome.Completed) {
      foreach (var diagnostic in result.Diagnostics)
        sink.Report(diagnostic);
      return false;
    }

    if (result.ExitCode != 0) {
      var stderr = Encoding.UTF8.GetString(result.Stderr.Span).Trim();
      sink.Report(Diagnostic.Create(BenchmarkDiagnostics.ProgramFailed, null, definition.Id, variant.Name, result.ExitCode) with { Cause = stderr.Length > 0 ? stderr : null });
      return false;
    }

    if (definition.ExpectStdout is { } expected) {
      var actual = Comparable(result.Stdout.Span);
      if (actual != Comparable(Encoding.UTF8.GetBytes(expected))) {
        sink.Report(BenchmarkDiagnostics.OutputMismatch(definition.Id, variant.Name, expected, actual));
        return false;
      }
    }

    return true;
  }

  /// <summary>Output as compared with <c>expectStdout</c>: line ends normalized, final line ends ignored.</summary>
  private static string Comparable(ReadOnlySpan<byte> output) => Encoding.UTF8.GetString(OutputComparison.NormalizeNewlines(output)).TrimEnd('\n');

  private static long Nanoseconds(TimeSpan time) => time.Ticks * 100;

  /// <summary>The bytes a collected path stands for: the file of that name, or every file below the directory of that name.</summary>
  private static long Collected(RunResult run, string path)
    => run.Collected.Where(entry => entry.Key == path || entry.Key.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal)).Sum(entry => (long)entry.Value.Length);

  private string Full(string path) => Path.Combine(host.RepositoryRoot, path);

  /// <summary>The files of the sources, placeholders replaced, by their path relative to the root; a directory contributes every file below it.</summary>
  private Dictionary<string, ReadOnlyMemory<byte>> Files(IEnumerable<string> sources) {
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
    foreach (var source in sources.Select(this.ReplacePlaceholders)) {
      var directory = this.Full(source);
      var names = Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(host.RepositoryRoot, file).Replace('\\', '/'))
        : [source];
      foreach (var name in names)
        files[name] = this.Read(name);
    }

    return files;
  }

  private byte[] Read(string path) {
    try {
      return File.ReadAllBytes(this.Full(path));
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      throw new BenchmarkFileException(path, 0, 0, $"'{path}' cannot be read: {exception.Message}");
    }
  }

  /// <summary>The size of a file, or of every file below a directory, relative to the repository root.</summary>
  private long Size(string path) {
    var full = this.Full(path);
    if (Directory.Exists(full))
      return Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
    if (File.Exists(full))
      return new FileInfo(full).Length;
    throw new BenchmarkFileException(path, 0, 0, $"'sizes.source' names '{path}', which does not exist");
  }

  private string[] Expand(IEnumerable<string> arguments)
    => [.. arguments.Select(argument => this.ReplacePlaceholders(argument.Replace(RootPlaceholder, host.RepositoryRoot, StringComparison.Ordinal)))];

  private string ReplacePlaceholders(string text)
    => host.Placeholders.Aggregate(text, (replaced, placeholder) => replaced.Replace(placeholder.Key, placeholder.Value, StringComparison.Ordinal));

}
