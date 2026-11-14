using System.Collections.Generic;

namespace Polypiler.Tooling.Toolchains;

/// <summary>How a toolchain compares with its pin.</summary>
public enum ToolchainStatus {

  /// <summary>Installed at exactly the pinned version.</summary>
  Ok,

  /// <summary>Not installed, not startable, or its probe timed out.</summary>
  Missing,

  /// <summary>Installed at another version, or printing none the pattern recognises.</summary>
  Mismatch,

  /// <summary>Not used on this platform, or an external toolchain in CI.</summary>
  NotOnThisPlatform

}

/// <summary>The outcome of probing one toolchain.</summary>
/// <param name="Key">The toolchain's key.</param>
/// <param name="Status">How it compares with its pin.</param>
/// <param name="ExpectedVersion">The version the lock pins.</param>
/// <param name="ActualVersion">The version found; <see langword="null"/> when none was.</param>
/// <param name="Path">The executable or package folder found; <see langword="null"/> when none was.</param>
public sealed record ToolchainProbe(string Key, ToolchainStatus Status, string ExpectedVersion, string? ActualVersion, string? Path) {

  /// <summary>Why the status is not <see cref="ToolchainStatus.Ok"/>, when there is more to say than the status.</summary>
  public string? Cause { get; init; }

  /// <summary>The probe's command line, as the lock writes it.</summary>
  public IReadOnlyList<string> Command { get; init; } = [];

  /// <summary>Whether the status lets a run go on: <see cref="ToolchainStatus.Ok"/> or <see cref="ToolchainStatus.NotOnThisPlatform"/>.</summary>
  public bool IsAcceptable => this.Status is ToolchainStatus.Ok or ToolchainStatus.NotOnThisPlatform;

  /// <summary>The diagnostic code and message of a <see cref="ToolchainStatus.Missing"/> or <see cref="ToolchainStatus.Mismatch"/> toolchain; <see langword="null"/> otherwise.</summary>
  public (string Code, string Message)? Problem => this.Status switch {
    ToolchainStatus.Missing => (ToolchainDiagnostic.NotInstalled,
      $"toolchain '{this.Key}' is not installed (probe: {string.Join(' ', this.Command)}){Suffix(this.Cause)}"),
    ToolchainStatus.Mismatch => (ToolchainDiagnostic.VersionMismatch,
      $"toolchain '{this.Key}' is {this.ActualVersion ?? "-"}, the lock pins {this.ExpectedVersion}{Suffix(this.Cause)}"),
    _ => null
  };

  private static string Suffix(string? cause) => cause == null ? "" : $": {cause}";

}
