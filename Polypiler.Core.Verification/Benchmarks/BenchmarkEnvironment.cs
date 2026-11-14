using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Polypiler.Core.Verification.Benchmarks;

/// <summary>
/// The machine and toolchains a result was measured on. Two results are compared only when their
/// fingerprints agree: a number from another machine or another toolchain version says nothing about
/// a change of the compiler.
/// </summary>
/// <param name="Os">The operating system: <c>linux</c>, <c>windows</c>, <c>macos</c> or <c>other</c>.</param>
/// <param name="OsVersion">The operating system's version.</param>
/// <param name="Architecture">The processor architecture, for example <c>x64</c>.</param>
/// <param name="CpuModel">The processor's model name, <c>unknown</c> where the system does not say.</param>
/// <param name="LogicalCores">The logical processors.</param>
/// <param name="Toolchains">The version of each toolchain the benchmark used, by its key in <c>tooling/toolchains.lock</c>.</param>
public sealed record BenchmarkEnvironment(string Os, string OsVersion, string Architecture, string CpuModel, int LogicalCores, IReadOnlyDictionary<string, string> Toolchains) {

  /// <summary>The version of each toolchain, in ordinal order of the keys.</summary>
  /// <exception cref="ArgumentNullException">The map is <see langword="null"/>.</exception>
  public IReadOnlyDictionary<string, string> Toolchains {
    get;
    init => field = Sorted(value);
  } = Sorted(Toolchains);

  private static SortedDictionary<string, string> Sorted(IReadOnlyDictionary<string, string> toolchains)
    => new(toolchains?.ToDictionary() ?? throw new ArgumentNullException(nameof(Toolchains)), StringComparer.Ordinal);

  /// <summary>The SHA-256 of <see cref="Canonical"/>, as lower-case hex.</summary>
  public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(this.Canonical())));

  /// <summary>The environment of this machine, with the given toolchain versions.</summary>
  /// <param name="toolchains">The version of each toolchain used.</param>
  public static BenchmarkEnvironment Detect(IReadOnlyDictionary<string, string> toolchains)
    => new(
      OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "other",
      Environment.OSVersion.Version.ToString(),
      RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
      CpuModelOf(OperatingSystem.IsLinux() ? ReadOrNull("/proc/cpuinfo") : null, Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")),
      Environment.ProcessorCount,
      toolchains);

  /// <summary>The processor's model: the first <c>model name</c> of <c>/proc/cpuinfo</c>, else the Windows processor identifier, else <c>unknown</c>.</summary>
  /// <param name="cpuinfo">The text of <c>/proc/cpuinfo</c>, or <see langword="null"/>.</param>
  /// <param name="processorIdentifier">The value of <c>PROCESSOR_IDENTIFIER</c>, or <see langword="null"/>.</param>
  public static string CpuModelOf(string? cpuinfo, string? processorIdentifier) {
    var line = cpuinfo?.Split('\n').FirstOrDefault(entry => entry.StartsWith("model name", StringComparison.Ordinal));
    if (line != null && line.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0)
      return line[(colon + 1)..].Trim();
    return string.IsNullOrWhiteSpace(processorIdentifier) ? "unknown" : processorIdentifier.Trim();
  }

  private static string? ReadOrNull(string path) {
    try {
      return File.ReadAllText(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return null;
    }
  }

  /// <summary>
  /// The first field in which two environments differ, in the order os, osVersion, architecture,
  /// cpuModel, logicalCores, then <c>toolchains.&lt;key&gt;</c> in ordinal key order, with both values;
  /// a toolchain only one side used reads <c>none</c> on the other.
  /// </summary>
  /// <param name="other">The other environment.</param>
  /// <returns>The field and the two values; <see langword="null"/> when the environments agree.</returns>
  public (string Field, string Mine, string Theirs)? FirstDifference(BenchmarkEnvironment other) {
    ArgumentNullException.ThrowIfNull(other);
    var fields = new (string, string, string)[] {
      ("os", this.Os, other.Os),
      ("osVersion", this.OsVersion, other.OsVersion),
      ("architecture", this.Architecture, other.Architecture),
      ("cpuModel", this.CpuModel, other.CpuModel),
      ("logicalCores", this.LogicalCores.ToString(CultureInfo.InvariantCulture), other.LogicalCores.ToString(CultureInfo.InvariantCulture))
    }.Concat(this.Toolchains.Keys.Union(other.Toolchains.Keys).Order(StringComparer.Ordinal)
      .Select(key => ($"toolchains.{key}", this.Toolchains.GetValueOrDefault(key) ?? "none", other.Toolchains.GetValueOrDefault(key) ?? "none")));
    foreach (var field in fields)
      if (field.Item2 != field.Item3)
        return field;

    return null;
  }

  /// <summary>The canonical JSON of the environment without its fingerprint: fixed property order, toolchains in ordinal key order, no white space.</summary>
  public string Canonical() {
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer))
      this.Write(writer, false);
    return Encoding.UTF8.GetString(buffer.ToArray());
  }

  /// <summary>Writes the environment as a JSON object, with its fingerprint when asked.</summary>
  internal void Write(Utf8JsonWriter writer, bool withFingerprint) {
    writer.WriteStartObject();
    writer.WriteString("os", this.Os);
    writer.WriteString("osVersion", this.OsVersion);
    writer.WriteString("architecture", this.Architecture);
    writer.WriteString("cpuModel", this.CpuModel);
    writer.WriteNumber("logicalCores", this.LogicalCores);
    writer.WriteStartObject("toolchains");
    foreach (var (key, version) in this.Toolchains)
      writer.WriteString(key, version);
    writer.WriteEndObject();
    if (withFingerprint)
      writer.WriteString("fingerprint", this.Fingerprint);
    writer.WriteEndObject();
  }

}
