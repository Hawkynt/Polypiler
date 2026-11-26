using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.Msil;

/// <summary>Whether the emitted .NET assembly is a class library or a runnable console executable.</summary>
public enum MsilOutputKind {

  /// <summary>A class library (<c>.dll</c>) without an entry point or runtime configuration.</summary>
  Library,

  /// <summary>A console executable (<c>.dll</c> with a CLI entry point plus <c>.runtimeconfig.json</c>).</summary>
  ConsoleExe

}

/// <summary>
/// The .NET MSIL target profile a module is emitted for: selects the target framework (<c>net10.0</c>
/// or <c>net8.0</c>) and output kind (<see cref="MsilOutputKind.Library"/> or <see cref="MsilOutputKind.ConsoleExe"/>).
/// </summary>
/// <param name="Name">The canonical profile name (<c>msil/net10</c> or <c>msil/net8</c>).</param>
/// <param name="TargetFramework">The target framework moniker (<c>net10.0</c> or <c>net8.0</c>).</param>
/// <param name="OutputKind">Whether to emit a library or a runnable console executable.</param>
public sealed record MsilProfile(string Name, string TargetFramework, MsilOutputKind OutputKind = MsilOutputKind.Library) {

  /// <summary>The .NET 10 profile (<c>msil/net10</c>).</summary>
  public static MsilProfile Net10 { get; } = new("msil/net10", "net10.0");

  /// <summary>The .NET 8 profile (<c>msil/net8</c>).</summary>
  public static MsilProfile Net8 { get; } = new("msil/net8", "net8.0");

  /// <summary>The default MSIL profile (<see cref="Net10"/>).</summary>
  public static MsilProfile Default => Net10;

  /// <summary>Every named MSIL profile.</summary>
  public static IReadOnlyList<string> Names { get; } = ["msil/net10", "msil/net8"];

  /// <summary>Every accepted value for the output kind.</summary>
  public static IReadOnlyList<string> OutputKindNames { get; } = ["library", "exe"];

  /// <summary>The <c>System.Runtime</c> assembly version targeted by this profile.</summary>
  internal Version RuntimeVersion => this.TargetFramework == "net8.0" ? new(8, 0, 0, 0) : new(10, 0, 0, 0);

  /// <summary>The <c>Microsoft.NETCore.App</c> framework version written into <c>.runtimeconfig.json</c>.</summary>
  internal string RuntimeFrameworkVersion => this.TargetFramework == "net8.0" ? "8.0.0" : "10.0.0";

  /// <summary>Parses a profile name and optional output kind, throwing on invalid input.</summary>
  /// <param name="profile">The profile name (<c>msil/net10</c>, <c>msil/net8</c>, or <see langword="null"/> for default).</param>
  /// <param name="outputKind">The output kind (<c>library</c>, <c>exe</c>, or <see langword="null"/> for default).</param>
  /// <returns>The parsed profile.</returns>
  /// <exception cref="ArgumentException">The profile or output kind is not recognized.</exception>
  public static MsilProfile Parse(string? profile = null, string? outputKind = null) {
    if (!TryParse(profile, outputKind, out var result, out var error))
      throw new ArgumentException(error);

    return result!;
  }

  /// <summary>Tries to parse a profile name and optional output kind.</summary>
  /// <param name="profile">The profile name (<c>msil/net10</c>, <c>msil/net8</c>, or <see langword="null"/> for default).</param>
  /// <param name="outputKind">The output kind (<c>library</c>, <c>exe</c>, or <see langword="null"/> for default).</param>
  /// <param name="result">The parsed profile, or <see langword="null"/> on failure.</param>
  /// <param name="error">A description of the problem, or <see langword="null"/> on success.</param>
  /// <returns><see langword="true"/> when all arguments are valid.</returns>
  public static bool TryParse(string? profile, string? outputKind, out MsilProfile? result, out string? error) {
    result = null;
    error = null;
    var baseProfile = profile switch {
      null or "msil/net10" => Net10,
      "msil/net8" => Net8,
      _ => null
    };
    if (baseProfile is null) {
      error = $"unknown MSIL profile '{profile}'; the profiles are msil/net10 and msil/net8";
      return false;
    }

    var kind = outputKind switch {
      null or "library" => MsilOutputKind.Library,
      "exe" => MsilOutputKind.ConsoleExe,
      _ => (MsilOutputKind)(-1)
    };
    if ((int)kind < 0) {
      error = $"unknown MSIL output kind '{outputKind}'; the output kinds are library and exe";
      return false;
    }

    result = baseProfile with { OutputKind = kind };
    return true;
  }

  /// <summary>Returns the canonical profile name.</summary>
  public override string ToString() => this.Name;

}

/// <summary>Optional overrides controlling .NET MSIL assembly emission.</summary>
public sealed record MsilEmitOptions {

  /// <summary>Default emission options.</summary>
  public static MsilEmitOptions Default { get; } = new();

  /// <summary>Overrides <see cref="MsilProfile.OutputKind"/> when specified.</summary>
  public MsilOutputKind? OutputKind { get; init; }

  /// <summary>Overrides the emitted assembly and file base name when specified.</summary>
  public string? AssemblyName { get; init; }

}
