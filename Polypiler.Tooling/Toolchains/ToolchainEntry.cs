using System.Collections.Generic;

namespace Polypiler.Tooling.Toolchains;

/// <summary>What a toolchain of the lock is and therefore how its version is found.</summary>
public enum ToolchainKind {

  /// <summary>An executable with a version command, run directly.</summary>
  Tool,

  /// <summary>A library installed into another toolchain; its probe is that host's command.</summary>
  Package,

  /// <summary>
  /// A NuGet package the build restores; its probe names the package id, and the probe looks for the
  /// pinned version in the NuGet global packages folder instead of starting a process.
  /// </summary>
  NuGet,

  /// <summary>A toolchain no hosted runner can install; in CI it probes as not on this platform.</summary>
  External

}

/// <summary>One toolchain of <c>tooling/toolchains.lock</c>.</summary>
/// <param name="Key">The key, <c>[a-z0-9][a-z0-9-]*</c>.</param>
/// <param name="Version">The exact version, the only place it is written.</param>
/// <param name="Probe">The command line that prints the version; its first element is resolved on <c>PATH</c> or from <see cref="Home"/>.</param>
/// <param name="VersionPattern">A .NET regular expression with a named group <c>version</c>, matched against each line the probe prints.</param>
/// <param name="Platforms">The platforms the toolchain is used on: <c>linux</c>, <c>windows</c>, <c>macos</c>.</param>
/// <param name="Licence">The SPDX identifier of the toolchain's licence.</param>
/// <param name="Source">Where CI obtains it.</param>
public sealed record ToolchainEntry(
  string Key,
  string Version,
  IReadOnlyList<string> Probe,
  string VersionPattern,
  IReadOnlyList<string> Platforms,
  string Licence,
  string Source
) {

  /// <summary>The environment variable naming an install folder, searched instead of <c>PATH</c> when it is set.</summary>
  public string? Home { get; init; }

  /// <summary>Whether the toolchain is used only as a reference and never to build outputs.</summary>
  public bool OracleOnly { get; init; }

  /// <summary>What the toolchain is; <see cref="ToolchainKind.Tool"/> unless the lock says otherwise.</summary>
  public ToolchainKind Kind { get; init; } = ToolchainKind.Tool;

  /// <summary>The key of the host toolchain a package is installed into.</summary>
  public string? Requires { get; init; }

  /// <summary>The line of the entry's key in the lock, counted from 1.</summary>
  public int Line { get; init; }

  /// <summary>The column of the entry's key in the lock, counted from 1.</summary>
  public int Column { get; init; }

}
