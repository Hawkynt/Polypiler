using System;
using System.IO;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Polypiler.Core.Verification.Running;

/// <summary>
/// The limits every step of a run is held to (PRD §7.2: no unbounded hangs or runaway memory). They
/// apply to each build step and to the execute step separately; a step that breaks one is killed with
/// its whole process tree.
/// </summary>
/// <param name="WallTime">How long a step may run.</param>
/// <param name="MemoryBytes">How much resident memory the step's process tree may hold at once.</param>
/// <param name="OutputBytes">How many bytes standard output and standard error may carry together; the capture stops there.</param>
/// <param name="Processes">How many processes the step's tree may have at once, the first one included.</param>
public sealed record RunLimits(TimeSpan WallTime, long MemoryBytes, long OutputBytes, int Processes) {

  /// <summary>The name of <c>tooling/ci/runner.yaml</c> inside the assembly.</summary>
  public const string ResourceName = "Polypiler.Core.Verification.Running.runner.yaml";

  /// <summary>The defaults of <c>tooling/ci/runner.yaml</c>: 60 s, 1 GiB, 16 MiB and 64 processes, ceilings rather than budgets.</summary>
  public static RunLimits Default { get; } = Load();

  /// <summary>How long a step may run.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The time is not positive.</exception>
  public TimeSpan WallTime { get; } = WallTime > TimeSpan.Zero ? WallTime : throw new ArgumentOutOfRangeException(nameof(WallTime), WallTime, "the wall time must be positive");

  /// <summary>How much resident memory the step's process tree may hold at once.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The size is not positive.</exception>
  public long MemoryBytes { get; } = MemoryBytes > 0 ? MemoryBytes : throw new ArgumentOutOfRangeException(nameof(MemoryBytes), MemoryBytes, "the memory limit must be positive");

  /// <summary>How many bytes standard output and standard error may carry together.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The size is negative.</exception>
  public long OutputBytes { get; } = OutputBytes >= 0 ? OutputBytes : throw new ArgumentOutOfRangeException(nameof(OutputBytes), OutputBytes, "the output limit must not be negative");

  /// <summary>How many processes the step's tree may have at once.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The count is not positive.</exception>
  public int Processes { get; } = Processes > 0 ? Processes : throw new ArgumentOutOfRangeException(nameof(Processes), Processes, "the process limit must be positive");

  /// <summary>Reads the limits of a <c>runner.yaml</c>.</summary>
  /// <param name="text">The file's text.</param>
  /// <returns>The limits it names.</returns>
  /// <exception cref="FormatException">The text is not a runner file of schema 1 with all four limits, or a limit is out of range.</exception>
  public static RunLimits Parse(string text) {
    RunnerFile? file;
    try {
      file = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build().Deserialize<RunnerFile?>(text);
    } catch (YamlException exception) {
      throw new FormatException($"runner.yaml({exception.Start.Line},{exception.Start.Column}): {exception.InnerException?.Message ?? exception.Message}", exception);
    }

    if (file is not { Schema: 1, Limits: { WallTimeMs: { } wall, MemoryBytes: { } memory, OutputBytes: { } output, Processes: { } processes } })
      throw new FormatException("runner.yaml: expected schema 1 and the limits wallTimeMs, memoryBytes, outputBytes and processes");

    try {
      return new(TimeSpan.FromMilliseconds(wall), memory, output, processes);
    } catch (ArgumentOutOfRangeException exception) {
      throw new FormatException($"runner.yaml: {exception.Message}", exception);
    }
  }

  private static RunLimits Load() {
    using var stream = typeof(RunLimits).Assembly.GetManifestResourceStream(ResourceName)!;
    using var reader = new StreamReader(stream);
    return Parse(reader.ReadToEnd());
  }

  /// <summary>The shape of <c>runner.yaml</c>.</summary>
  private sealed class RunnerFile {

    public int Schema { get; set; }

    public LimitsSection? Limits { get; set; }

  }

  /// <summary>The <c>limits</c> section of <c>runner.yaml</c>.</summary>
  private sealed class LimitsSection {

    public long? WallTimeMs { get; set; }

    public long? MemoryBytes { get; set; }

    public long? OutputBytes { get; set; }

    public int? Processes { get; set; }

  }

}
