using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.VisualBasic;

/// <summary>
/// The Visual Basic a module is written for. The classic dialects are named
/// <c>visualbasic/&lt;dialect&gt;</c>: <c>vb6</c> (Visual Basic 6.0), <c>vba6</c> (VBA 6 of Office 2000
/// to 2007), <c>vba7</c> (VBA 7 of Office 2010 and later, code that compiles on its 32-bit and its
/// 64-bit hosts) and <c>vba7-64</c> (VBA 7 on a 64-bit host, which alone has <c>LongLong</c>); what
/// each has is the <see cref="VisualBasicCapabilities"/>, the language of a standard module is
/// otherwise the same in all four ([MS-VBAL] 1.0). Visual Basic .NET is named
/// <c>vbnet/vb&lt;version&gt;</c>, one profile per language version from Visual Basic 9 to 16.9, for
/// a target framework that <c>vbnet/vb&lt;version&gt;/&lt;framework&gt;</c> names (.NET 10 unless named);
/// what a version and a framework have is the <see cref="VbNetCapabilities"/>.
/// </summary>
public sealed record VisualBasicProfile {

  /// <summary>The prefix of every classic profile name.</summary>
  public const string Prefix = "visualbasic/";

  /// <summary>The prefix of every Visual Basic .NET profile name.</summary>
  public const string NetPrefix = "vbnet/vb";

  /// <summary>The dialect name of every Visual Basic .NET profile.</summary>
  public const string NetDialect = "vbnet";

  private VisualBasicProfile(string dialect, string title, string? languageVersion = null, VisualBasicFramework? framework = null) {
    this.Dialect = dialect;
    this.Title = title;
    this.LanguageVersion = languageVersion;
    this.Framework = framework;
  }

  /// <summary>The dialect: <c>vb6</c>, <c>vba6</c>, <c>vba7</c>, <c>vba7-64</c> or <c>vbnet</c>.</summary>
  public string Dialect { get; }

  /// <summary>What the dialect is, as a header states it.</summary>
  public string Title { get; }

  /// <summary>The language version of Visual Basic .NET, such as <c>16.9</c>; <see langword="null"/> for a classic dialect.</summary>
  public string? LanguageVersion { get; }

  /// <summary>The target framework of Visual Basic .NET; <see langword="null"/> for a classic dialect.</summary>
  public VisualBasicFramework? Framework { get; }

  /// <summary>Whether the profile is one of Visual Basic .NET.</summary>
  public bool IsNet => this.LanguageVersion is not null;

  /// <summary>
  /// Whether the project of a Visual Basic .NET profile is compiled with <c>RemoveIntegerChecks</c>, so
  /// that its operators and conversion functions wrap (the default), or with integer checks on, so that
  /// they throw an <c>OverflowException</c> and the wrapping arithmetic of the IR is written out.
  /// </summary>
  public bool RemoveIntegerChecks { get; init; } = true;

  /// <summary>
  /// The name of the profile: <c>visualbasic/&lt;dialect&gt;</c>, or <c>vbnet/vb&lt;version&gt;</c> with
  /// <c>/&lt;framework&gt;</c> after it for a framework other than the default.
  /// </summary>
  public string Name => !this.IsNet ? Prefix + this.Dialect
    : this.Framework == VisualBasicFramework.Default ? NetPrefix + this.LanguageVersion : $"{NetPrefix}{this.LanguageVersion}/{this.Framework!.Name}";

  /// <summary>The language version as an ordered number: <c>16.9</c> is 16.9, <c>9</c> is 9; 0 for a classic dialect.</summary>
  public Version Version => this.IsNet ? System.Version.Parse(this.LanguageVersion!.Contains('.', StringComparison.Ordinal) ? this.LanguageVersion : this.LanguageVersion + ".0") : new Version(0, 0);

  /// <summary>Visual Basic 6.0.</summary>
  public static VisualBasicProfile Vb6 { get; } = new("vb6", "Visual Basic 6.0");

  /// <summary>Visual Basic for Applications 6 (Office 2000 to 2007).</summary>
  public static VisualBasicProfile Vba6 { get; } = new("vba6", "Visual Basic for Applications 6");

  /// <summary>Visual Basic for Applications 7, for its 32-bit and its 64-bit hosts alike.</summary>
  public static VisualBasicProfile Vba7 { get; } = new("vba7", "Visual Basic for Applications 7");

  /// <summary>Visual Basic for Applications 7 on a 64-bit host (Win64), which has <c>LongLong</c>.</summary>
  public static VisualBasicProfile Vba7x64 { get; } = new("vba7-64", "Visual Basic for Applications 7, 64-bit host");

  /// <summary>Every classic profile, oldest first.</summary>
  public static IReadOnlyList<VisualBasicProfile> All { get; } = [Vb6, Vba6, Vba7, Vba7x64];

  /// <summary>
  /// The language versions of Visual Basic .NET, oldest first, as the compiler's <c>LangVersion</c>
  /// takes them: 9 (Visual Studio 2008) to 16.9, the last.
  /// </summary>
  public static IReadOnlyList<string> NetLanguageVersions { get; } = ["9", "10", "11", "12", "14", "15", "15.3", "15.5", "16", "16.9"];

  /// <summary>Every Visual Basic .NET profile for the default framework, oldest first.</summary>
  public static IReadOnlyList<VisualBasicProfile> NetAll { get; } = [.. NetLanguageVersions.Select(version => Net(version, VisualBasicFramework.Default))];

  /// <summary>Visual Basic 16.9 on .NET 10, the newest Visual Basic .NET.</summary>
  public static VisualBasicProfile VbNetLatest => NetAll[^1];

  /// <summary>The names of every profile: the classic ones, then those of Visual Basic .NET, oldest first.</summary>
  public static IReadOnlyList<string> Names { get; } = [.. All.Concat(NetAll).Select(profile => profile.Name)];

  /// <summary>The profile the translation writes when none is named: VBA 7, which runs on every current Office host.</summary>
  public static VisualBasicProfile Default => Vba7;

  private static VisualBasicProfile Net(string version, VisualBasicFramework framework)
    => new(NetDialect, $"Visual Basic {version} on {framework.Title}", version, framework);

  /// <summary>
  /// Reads a profile name: <c>visualbasic/vba7</c> or the dialect alone, <c>vba7</c>; <c>vbnet/vb16.9</c>
  /// or <c>vbnet/vb16.9/net4.8</c>; in any case.
  /// </summary>
  /// <param name="name">The name; <see langword="null"/> for the default.</param>
  /// <param name="profile">The profile, or <see langword="null"/>.</param>
  /// <param name="error">What is wrong with the name, or <see langword="null"/>.</param>
  /// <returns>Whether the name is a profile.</returns>
  public static bool TryParse(string? name, out VisualBasicProfile? profile, out string? error) {
    error = null;
    if (name is null) {
      profile = Default;
      return true;
    }

    if (name.StartsWith(NetPrefix, StringComparison.OrdinalIgnoreCase))
      return TryParseNet(name, out profile, out error);

    var dialect = name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? name[Prefix.Length..] : name;
    profile = All.FirstOrDefault(candidate => string.Equals(candidate.Dialect, dialect, StringComparison.OrdinalIgnoreCase));
    if (profile is not null)
      return true;

    error = $"unknown Visual Basic profile '{name}'; the profiles are {string.Join(", ", Names)}";
    return false;
  }

  private static bool TryParseNet(string name, out VisualBasicProfile? profile, out string? error) {
    profile = null;
    var parts = name[NetPrefix.Length..].Split('/');
    var version = NetLanguageVersions.FirstOrDefault(candidate => candidate == parts[0]);
    if (version is null || parts.Length > 2) {
      error = $"unknown Visual Basic .NET profile '{name}'; the language versions are {string.Join(", ", NetLanguageVersions)}, optionally with a framework after a slash";
      return false;
    }

    var framework = parts.Length == 1 ? VisualBasicFramework.Default : VisualBasicFramework.All.FirstOrDefault(candidate => string.Equals(candidate.Name, parts[1], StringComparison.OrdinalIgnoreCase));
    if (framework is null) {
      error = $"unknown target framework '{parts[1]}' of the Visual Basic .NET profile '{name}'; the frameworks are {string.Join(", ", VisualBasicFramework.All.Select(candidate => candidate.Name))}";
      return false;
    }

    error = null;
    profile = framework == VisualBasicFramework.Default ? NetAll[NetLanguageVersions.ToList().IndexOf(version)] : Net(version, framework);
    return true;
  }

  /// <summary>Reads a profile name as <see cref="TryParse"/> does.</summary>
  /// <param name="name">The name; the default profile for <see langword="null"/>.</param>
  /// <returns>The profile.</returns>
  /// <exception cref="ArgumentException">The name is no profile.</exception>
  public static VisualBasicProfile Parse(string? name)
    => TryParse(name, out var profile, out var error) ? profile! : throw new ArgumentException(error, nameof(name));

  /// <summary>The name of the profile.</summary>
  public override string ToString() => this.Name;

}

/// <summary>
/// A target framework of Visual Basic .NET, by the moniker a project file names. The frameworks are
/// ordered, and whatever one of them has, every later one has too, which is what lets
/// <see cref="VbNetCapabilities"/> name one first framework per API.
/// </summary>
/// <param name="Name">The moniker, as the C# backend's profiles name the same frameworks.</param>
/// <param name="Title">The framework as its documentation calls it.</param>
/// <param name="Moniker">The target framework moniker of a project file.</param>
/// <param name="Rank">The position among <see cref="All"/>.</param>
public sealed record VisualBasicFramework(string Name, string Title, string Moniker, int Rank) {

  /// <summary>.NET Framework 2.0.</summary>
  public static VisualBasicFramework Net20 { get; } = new("net2.0", ".NET Framework 2.0", "net20", 0);

  /// <summary>.NET Framework 3.5.</summary>
  public static VisualBasicFramework Net35 { get; } = new("net3.5", ".NET Framework 3.5", "net35", 1);

  /// <summary>.NET Framework 4.6.</summary>
  public static VisualBasicFramework Net46 { get; } = new("net4.6", ".NET Framework 4.6", "net46", 2);

  /// <summary>.NET Framework 4.8.</summary>
  public static VisualBasicFramework Net48 { get; } = new("net4.8", ".NET Framework 4.8", "net48", 3);

  /// <summary>.NET Standard 2.0.</summary>
  public static VisualBasicFramework NetStandard20 { get; } = new("netstandard2.0", ".NET Standard 2.0", "netstandard2.0", 4);

  /// <summary>.NET Standard 2.1.</summary>
  public static VisualBasicFramework NetStandard21 { get; } = new("netstandard2.1", ".NET Standard 2.1", "netstandard2.1", 5);

  /// <summary>.NET Core 3.1.</summary>
  public static VisualBasicFramework NetCoreApp31 { get; } = new("netcoreapp3.1", ".NET Core 3.1", "netcoreapp3.1", 6);

  /// <summary>.NET 8.</summary>
  public static VisualBasicFramework Net80 { get; } = new("net8.0", ".NET 8", "net8.0", 7);

  /// <summary>.NET 10.</summary>
  public static VisualBasicFramework Net100 { get; } = new("net10.0", ".NET 10", "net10.0", 8);

  /// <summary>Every framework, oldest first.</summary>
  public static IReadOnlyList<VisualBasicFramework> All { get; } = [Net20, Net35, Net46, Net48, NetStandard20, NetStandard21, NetCoreApp31, Net80, Net100];

  /// <summary>The framework a profile that names none is written for: .NET 10.</summary>
  public static VisualBasicFramework Default => Net100;

}
