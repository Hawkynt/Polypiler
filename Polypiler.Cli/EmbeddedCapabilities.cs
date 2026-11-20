using System;
using System.IO;
using Polypiler.Core.Verification.Capabilities;

namespace Polypiler.Cli;

/// <summary>
/// The capability report the executable was built with: <c>docs/capabilities.json</c>, which the
/// build embeds as <see cref="ResourceName"/> and refuses to go without, so that the executable states
/// exactly what the gate records of its commit prove.
/// </summary>
public static class EmbeddedCapabilities {

  /// <summary>The name of the resource that carries the report.</summary>
  public const string ResourceName = "Polypiler.Cli.capabilities.json";

  /// <summary>Reads the embedded report.</summary>
  /// <returns>The report.</returns>
  /// <exception cref="InvalidOperationException">The executable carries no report.</exception>
  /// <exception cref="InvalidDataException">The report breaks its schema.</exception>
  public static CapabilityReport Load() => Load(() => typeof(EmbeddedCapabilities).Assembly.GetManifestResourceStream(ResourceName));

  /// <summary>Reads a report from the bytes a stream gives.</summary>
  /// <param name="open">Opens the bytes, or gives <see langword="null"/> when there are none.</param>
  /// <returns>The report.</returns>
  /// <exception cref="InvalidOperationException">There are no bytes; the message names <see cref="ResourceName"/>.</exception>
  /// <exception cref="InvalidDataException">The bytes break the schema of the report.</exception>
  public static CapabilityReport Load(Func<Stream?> open) {
    ArgumentNullException.ThrowIfNull(open);
    using var stream = open() ?? throw new InvalidOperationException($"The executable carries no '{ResourceName}'; it was built without docs/capabilities.json.");
    return CapabilityReport.Load(stream);
  }

}
