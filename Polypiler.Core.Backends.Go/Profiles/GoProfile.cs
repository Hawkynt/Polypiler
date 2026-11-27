using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Polypiler.Core.Backends.Go;

/// <summary>
/// The Go a package is written for: a language version of Go 1 from 1.18, the first with type
/// parameters, to 1.27, the version <c>tooling/toolchains.lock</c> pins; named <c>go1.18</c> to
/// <c>go1.27</c> as the <c>go</c> command names them. What each version has is the
/// <see cref="GoCapabilities"/>; the version is the <c>go</c> directive of the generated
/// <c>go.mod</c>, which also selects the language's semantics (per-iteration loop variables from 1.22),
/// and the output builds with the version named and every later one. <c>int</c> and <c>uint</c> are
/// 64 bits wide in every profile (<see cref="IntWidth"/>), the width of every 64-bit <c>GOARCH</c>; the
/// generated code names them only for what Go's own functions take and give - a length, a count of
/// bits - and converts at once.
/// </summary>
public sealed record GoProfile {

  /// <summary>The oldest minor version of Go 1 a profile names.</summary>
  public const int OldestMinor = 18;

  /// <summary>The newest minor version of Go 1 a profile names: the pinned toolchain's.</summary>
  public const int NewestMinor = 27;

  private GoProfile(int minor) => this.Minor = minor;

  /// <summary>The minor version of Go 1, 18 to 27.</summary>
  public int Minor { get; }

  /// <summary>The name of the profile, <c>go1.&lt;minor&gt;</c>.</summary>
  public string Name => "go" + this.Version;

  /// <summary>The language version as the <c>go</c> directive of <c>go.mod</c> writes it, <c>1.&lt;minor&gt;</c>.</summary>
  public string Version => "1." + this.Minor.ToString(CultureInfo.InvariantCulture);

  /// <summary>The width of <c>int</c> and <c>uint</c> in bits.</summary>
  public int IntWidth => 64;

  /// <summary>Every profile, oldest first.</summary>
  public static IReadOnlyList<GoProfile> All { get; } = [.. Enumerable.Range(OldestMinor, NewestMinor - OldestMinor + 1).Select(minor => new GoProfile(minor))];

  /// <summary>
  /// The profiles <c>polypiler targets</c> lists: each version that brought a construct of the
  /// <see cref="GoCapabilities"/> table, and the newest. Every other version in between is a profile
  /// too and writes what the listed one below it writes.
  /// </summary>
  public static IReadOnlyList<string> Names { get; } = ["go1.18", "go1.21", "go1.22", "go1.23", "go1.27"];

  /// <summary>The profile the translation writes when none is named: the version <c>tooling/toolchains.lock</c> pins.</summary>
  public static GoProfile Default { get; } = All[^1];

  /// <summary>Reads a profile name: <c>go1.18</c> to <c>go1.27</c>, <c>1.18</c> to <c>1.27</c>, or <c>go/1</c> for the default one.</summary>
  /// <param name="name">The name; <see langword="null"/> for the default profile.</param>
  /// <param name="profile">The profile, or <see langword="null"/>.</param>
  /// <param name="error">What is wrong with the name, or <see langword="null"/>.</param>
  /// <returns>Whether the name is a profile.</returns>
  public static bool TryParse(string? name, out GoProfile? profile, out string? error) {
    error = null;
    if (name is null or "go/1") {
      profile = Default;
      return true;
    }

    var version = name.StartsWith("go", StringComparison.Ordinal) ? name[2..] : name;
    profile = All.FirstOrDefault(candidate => candidate.Version == version);
    if (profile is not null)
      return true;

    error = $"unknown Go profile '{name}'; the profiles are go1.{OldestMinor} to go1.{NewestMinor} and go/1";
    return false;
  }

  /// <summary>Reads a profile name as <see cref="TryParse"/> does.</summary>
  /// <param name="name">The name; the default profile for <see langword="null"/>.</param>
  /// <returns>The profile.</returns>
  /// <exception cref="ArgumentException">The name is no profile.</exception>
  public static GoProfile Parse(string? name)
    => TryParse(name, out var profile, out var error) ? profile! : throw new ArgumentException(error, nameof(name));

  /// <summary>The name of the profile.</summary>
  public override string ToString() => this.Name;

}
