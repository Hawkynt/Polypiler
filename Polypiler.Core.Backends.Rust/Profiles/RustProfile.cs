using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Polypiler.Core.Backends.Rust;

/// <summary>What a trap of the IR is in generated Rust (ADR 0008).</summary>
public enum RustTrapPolicy {

  /// <summary>A <c>panic!</c> named after the IR trap; a function keeps its plain result type.</summary>
  Panic,

  /// <summary>
  /// An error value: every function a trap can leave returns a <c>Result</c>, every trapping operation
  /// is an explicit check that hands the trap's error on, and nothing panics.
  /// </summary>
  Result

}

/// <summary>
/// What generated Rust is written for: an edition and the oldest compiler that has to build it, its
/// minimum supported Rust version (MSRV). Each edition comes with the release that stabilized it as
/// its MSRV; the MSRV can be raised, never lowered below that. The edition decides which words are
/// keywords; the MSRV decides which methods of the standard library a crate may call, row by row of
/// <see cref="RustCapabilities"/>.
/// </summary>
public sealed class RustProfile {

  /// <summary>The 2018 edition with Rust 1.31, the release that stabilized it.</summary>
  public static RustProfile Rust2018 { get; } = new("rust2018", 2018, new Version(1, 31));

  /// <summary>The 2021 edition with Rust 1.56.</summary>
  public static RustProfile Rust2021 { get; } = new("rust2021", 2021, new Version(1, 56));

  /// <summary>The 2024 edition with Rust 1.85.</summary>
  public static RustProfile Rust2024 { get; } = new("rust2024", 2024, new Version(1, 85));

  /// <summary>Every edition, oldest first.</summary>
  public static IReadOnlyList<RustProfile> All { get; } = [Rust2018, Rust2021, Rust2024];

  /// <summary>The profile that is used when none is named.</summary>
  public static RustProfile Default => Rust2024;

  private RustProfile(string name, int edition, Version minimum, Version? version = null, RustTrapPolicy trapPolicy = RustTrapPolicy.Panic) {
    this.Name = name;
    this.Edition = edition;
    this.MinimumRustVersion = minimum;
    this.RustVersion = version ?? minimum;
    this.TrapPolicy = trapPolicy;
  }

  /// <summary>The names <c>--trap-policy</c> takes, in the order of <see cref="RustTrapPolicy"/>.</summary>
  public static IReadOnlyList<string> TrapPolicies { get; } = ["panic", "result"];

  /// <summary>What a trap is in the generated code: a panic, the default, or an error value.</summary>
  public RustTrapPolicy TrapPolicy { get; }

  /// <summary>The same profile with another trap policy.</summary>
  /// <param name="policy">The trap policy.</param>
  /// <returns>The profile.</returns>
  public RustProfile WithTrapPolicy(RustTrapPolicy policy) => new(this.Name, this.Edition, this.MinimumRustVersion, this.RustVersion, policy);

  /// <summary>The name <c>--profile</c> takes: <c>rust2018</c>, <c>rust2021</c> or <c>rust2024</c>.</summary>
  public string Name { get; }

  /// <summary>The edition, the year <c>Cargo.toml</c> names.</summary>
  public int Edition { get; }

  /// <summary>The release that stabilized the edition, below which the profile cannot go.</summary>
  public Version MinimumRustVersion { get; }

  /// <summary>The oldest release that must build the crate: the <c>rust-version</c> of <c>Cargo.toml</c>.</summary>
  public Version RustVersion { get; }

  /// <summary>The MSRV as <c>Cargo.toml</c> writes it: <c>1.85</c>.</summary>
  public string RustVersionName => $"{this.RustVersion.Major}.{this.RustVersion.Minor}";

  /// <summary>Whether the crate may use what a release of Rust stabilized.</summary>
  /// <param name="version">The release, such as 1.77.</param>
  public bool Has(Version version) => this.RustVersion >= version;

  /// <summary>The same edition for a newer compiler.</summary>
  /// <param name="version">The MSRV, at least <see cref="MinimumRustVersion"/>.</param>
  /// <returns>The profile.</returns>
  /// <exception cref="ArgumentException">The version is older than the edition.</exception>
  public RustProfile WithRustVersion(Version version) {
    ArgumentNullException.ThrowIfNull(version);
    if (version < this.MinimumRustVersion)
      throw new ArgumentException($"Rust {version.Major}.{version.Minor} is older than the {this.Edition} edition, which Rust {this.RustVersionName} brought.", nameof(version));

    return new(this.Name, this.Edition, this.MinimumRustVersion, new Version(version.Major, version.Minor), this.TrapPolicy);
  }

  /// <summary>Reads a profile name and an MSRV as the command line gives them.</summary>
  /// <param name="name">The profile, <see langword="null"/> for <see cref="Default"/>.</param>
  /// <param name="rustVersion">The MSRV such as <c>1.77</c>, <see langword="null"/> for the one of the edition.</param>
  /// <param name="profile">The profile, when the names are valid.</param>
  /// <param name="error">Why they are not, otherwise.</param>
  /// <returns>Whether the names are valid.</returns>
  public static bool TryParse(string? name, string? rustVersion, out RustProfile? profile, out string? error) {
    profile = null;
    var edition = name is null ? Default : All.FirstOrDefault(candidate => candidate.Name == name);
    if (edition is null) {
      error = $"unknown Rust profile '{name}'; the profiles are {string.Join(", ", All.Select(candidate => candidate.Name))}";
      return false;
    }

    if (rustVersion is null) {
      (profile, error) = (edition, null);
      return true;
    }

    if (!TryVersion(rustVersion, out var version)) {
      error = $"'{rustVersion}' is not a Rust version of the form 1.<minor>";
      return false;
    }

    if (version < edition.MinimumRustVersion) {
      error = $"Rust {rustVersion} is older than the {edition.Edition} edition of the profile '{edition.Name}', which needs {edition.RustVersionName}";
      return false;
    }

    (profile, error) = (edition.WithRustVersion(version), null);
    return true;
  }

  /// <summary>Reads a profile name, an MSRV and a trap policy as the command line gives them.</summary>
  /// <param name="name">The profile, <see langword="null"/> for <see cref="Default"/>.</param>
  /// <param name="rustVersion">The MSRV, <see langword="null"/> for the one of the edition.</param>
  /// <param name="trapPolicy">The trap policy, <c>panic</c> or <c>result</c>; <see langword="null"/> for <c>panic</c>.</param>
  /// <param name="profile">The profile, when the names are valid.</param>
  /// <param name="error">Why they are not, otherwise.</param>
  /// <returns>Whether the names are valid.</returns>
  public static bool TryParse(string? name, string? rustVersion, string? trapPolicy, out RustProfile? profile, out string? error) {
    if (!TryParse(name, rustVersion, out profile, out error))
      return false;

    var index = trapPolicy is null ? 0 : TrapPolicies.ToList().IndexOf(trapPolicy);
    if (index < 0) {
      (profile, error) = (null, $"unknown trap policy '{trapPolicy}'; the trap policies are {string.Join(" and ", TrapPolicies)}");
      return false;
    }

    profile = profile!.WithTrapPolicy((RustTrapPolicy)index);
    return true;
  }

  /// <summary>A version of the form <c>1.77</c>; a patch number is accepted and dropped.</summary>
  private static bool TryVersion(string text, out Version version) {
    version = new Version(0, 0);
    var parts = text.Split('.');
    if (parts.Length is < 2 or > 3 || parts[0] != "1"
      || !parts.Skip(1).All(part => part.Length > 0 && part.All(char.IsAsciiDigit))
      || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
      return false;

    version = new Version(1, minor);
    return true;
  }

  /// <summary>The profile as the header of a generated file states it: <c>rust2024 (Rust 1.85)</c>; under the result trap policy <c>rust2024 (Rust 1.85, trap policy result)</c>.</summary>
  public override string ToString() => $"{this.Name} (Rust {this.RustVersionName}{(this.TrapPolicy == RustTrapPolicy.Result ? ", trap policy result" : "")})";

}
