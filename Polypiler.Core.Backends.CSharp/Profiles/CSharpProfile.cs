using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// What generated C# is written for: a target framework and the version of the language. Each
/// profile fixes the framework and the newest language version the compiler of that framework's time
/// understands; the language version can be raised, never lowered. The profiles are ordered, and
/// whatever one of them has, every later one has too — which is what lets
/// <see cref="CSharpCapabilities"/> name one first profile per API.
/// </summary>
public sealed class CSharpProfile {

  /// <summary>.NET Framework 2.0 with C# 2.</summary>
  public static CSharpProfile Net20 { get; } = new(0, "net2.0", ".NET Framework 2.0", new Version(2, 0));

  /// <summary>.NET Framework 3.5 with C# 3.</summary>
  public static CSharpProfile Net35 { get; } = new(1, "net3.5", ".NET Framework 3.5", new Version(3, 0));

  /// <summary>.NET Framework 4.6 with C# 6.</summary>
  public static CSharpProfile Net46 { get; } = new(2, "net4.6", ".NET Framework 4.6", new Version(6, 0));

  /// <summary>.NET Framework 4.8 with C# 7.3.</summary>
  public static CSharpProfile Net48 { get; } = new(3, "net4.8", ".NET Framework 4.8", new Version(7, 3));

  /// <summary>.NET Standard 2.0 with C# 7.3.</summary>
  public static CSharpProfile NetStandard20 { get; } = new(4, "netstandard2.0", ".NET Standard 2.0", new Version(7, 3));

  /// <summary>.NET Standard 2.1 with C# 8.</summary>
  public static CSharpProfile NetStandard21 { get; } = new(5, "netstandard2.1", ".NET Standard 2.1", new Version(8, 0));

  /// <summary>.NET Core 3.1 with C# 8.</summary>
  public static CSharpProfile NetCoreApp31 { get; } = new(6, "netcoreapp3.1", ".NET Core 3.1", new Version(8, 0));

  /// <summary>.NET 8 with C# 12.</summary>
  public static CSharpProfile Net80 { get; } = new(7, "net8.0", ".NET 8", new Version(12, 0));

  /// <summary>.NET 10 with C# 14.</summary>
  public static CSharpProfile Net100 { get; } = new(8, "net10.0", ".NET 10", new Version(14, 0));

  /// <summary>Every profile, oldest first.</summary>
  public static IReadOnlyList<CSharpProfile> All { get; } = [Net20, Net35, Net46, Net48, NetStandard20, NetStandard21, NetCoreApp31, Net80, Net100];

  /// <summary>The profile that is used when none is named.</summary>
  public static CSharpProfile Default => Net100;

  /// <summary>The versions of C#, oldest first (the C# language version history).</summary>
  public static IReadOnlyList<Version> LanguageVersions { get; } = [
    new(1, 0), new(2, 0), new(3, 0), new(4, 0), new(5, 0), new(6, 0), new(7, 0), new(7, 1), new(7, 2), new(7, 3),
    new(8, 0), new(9, 0), new(10, 0), new(11, 0), new(12, 0), new(13, 0), new(14, 0)
  ];

  private CSharpProfile(int rank, string name, string framework, Version minimum, Version? language = null) {
    this.Rank = rank;
    this.Name = name;
    this.Framework = framework;
    this.MinimumLanguageVersion = minimum;
    this.LanguageVersion = language ?? minimum;
  }

  /// <summary>The name <c>--profile</c> takes, which is the target framework moniker.</summary>
  public string Name { get; }

  /// <summary>The framework as its documentation calls it.</summary>
  public string Framework { get; }

  /// <summary>The language version of the framework's time, below which the profile cannot go.</summary>
  public Version MinimumLanguageVersion { get; }

  /// <summary>The language version the code is written in.</summary>
  public Version LanguageVersion { get; }

  /// <summary>The language version as it is written: <c>7.3</c>, <c>8</c>.</summary>
  public string LanguageVersionName => NameOf(this.LanguageVersion);

  /// <summary>The position among <see cref="All"/>: a later framework has what an earlier one has.</summary>
  internal int Rank { get; }

  /// <summary>A language version as it is written, without a minor version of zero.</summary>
  /// <param name="version">The version.</param>
  /// <returns><c>7.3</c> for 7.3 and <c>8</c> for 8.0.</returns>
  public static string NameOf(Version version)
    => version.Minor == 0 ? version.Major.ToString(CultureInfo.InvariantCulture) : version.ToString(2);

  /// <summary>Finds a profile by name and applies a language version to it.</summary>
  /// <param name="name">The name of the profile, as in <see cref="Name"/>.</param>
  /// <param name="languageVersion">The language version to raise the profile to, or <see langword="null"/> to keep its own.</param>
  /// <param name="profile">The profile, when there is no problem.</param>
  /// <param name="problem">What is wrong, when the profile is unknown or the version is none or too low.</param>
  /// <returns><see langword="true"/> when the profile exists and takes the language version.</returns>
  public static bool TryParse(string name, string? languageVersion, out CSharpProfile profile, out string problem) {
    profile = Default;
    problem = "";
    var found = All.FirstOrDefault(candidate => candidate.Name == name);
    if (found is null) {
      problem = $"unknown C# profile '{name}'; the profiles are {string.Join(", ", All.Select(candidate => $"'{candidate.Name}'"))}";
      return false;
    }

    profile = found;
    if (languageVersion is null)
      return true;

    var version = LanguageVersions.FirstOrDefault(candidate => languageVersion == NameOf(candidate) || languageVersion == candidate.ToString(2));
    if (version is null) {
      problem = $"'{languageVersion}' is not a C# language version; the versions are {string.Join(", ", LanguageVersions.Select(NameOf))}";
      return false;
    }

    if (version < found.MinimumLanguageVersion) {
      problem = $"the C# profile '{found.Name}' is written in C# {NameOf(found.MinimumLanguageVersion)} or later, not in C# {NameOf(version)}";
      return false;
    }

    profile = new CSharpProfile(found.Rank, found.Name, found.Framework, found.MinimumLanguageVersion, version);
    return true;
  }

  /// <summary>Finds a profile by name and applies a language version to it.</summary>
  /// <param name="name">The name of the profile, as in <see cref="Name"/>.</param>
  /// <param name="languageVersion">The language version to raise the profile to, or <see langword="null"/> to keep its own.</param>
  /// <returns>The profile.</returns>
  /// <exception cref="ArgumentException">The profile is unknown, or the language version is none or below the profile's.</exception>
  public static CSharpProfile Parse(string name, string? languageVersion = null)
    => TryParse(name, languageVersion, out var profile, out var problem) ? profile : throw new ArgumentException(problem, nameof(name));

  /// <summary>The profile as the header of a generated file names it.</summary>
  public override string ToString() => $"{this.Name}, C# {this.LanguageVersionName}";

}
