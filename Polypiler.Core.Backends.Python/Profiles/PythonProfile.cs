using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Polypiler.Core.Backends.Python;

/// <summary>
/// The Python a module is written for: a version of the language from 3.8 to 3.14, named
/// <c>py3.8</c> to <c>py3.14</c>. What each version has is the <see cref="PythonCapabilities"/>;
/// the output runs on the version named and on every later one.
/// </summary>
public sealed record PythonProfile {

  /// <summary>The oldest minor version of Python 3 a profile names.</summary>
  public const int OldestMinor = 8;

  /// <summary>The newest minor version of Python 3 a profile names.</summary>
  public const int NewestMinor = 14;

  private PythonProfile(int minor) => this.Minor = minor;

  /// <summary>The minor version of Python 3, 8 to 14.</summary>
  public int Minor { get; }

  /// <summary>The name of the profile, <c>py3.&lt;minor&gt;</c>.</summary>
  public string Name => $"py3.{this.Minor.ToString(CultureInfo.InvariantCulture)}";

  /// <summary>The version of Python as it is written, <c>3.&lt;minor&gt;</c>.</summary>
  public string Version => $"3.{this.Minor.ToString(CultureInfo.InvariantCulture)}";

  /// <summary>Every profile, oldest first.</summary>
  public static IReadOnlyList<PythonProfile> All { get; } = [.. Enumerable.Range(OldestMinor, NewestMinor - OldestMinor + 1).Select(minor => new PythonProfile(minor))];

  /// <summary>The names of every profile, oldest first.</summary>
  public static IReadOnlyList<string> Names { get; } = [.. All.Select(profile => profile.Name)];

  /// <summary>The profile the translation writes when none is named: the version <c>tooling/toolchains.lock</c> pins.</summary>
  public static PythonProfile Default { get; } = All[^1];

  /// <summary>Reads a profile name: <c>py3.8</c> to <c>py3.14</c>, or <c>3.8</c> to <c>3.14</c>.</summary>
  /// <param name="name">The name.</param>
  /// <param name="profile">The profile, or <see langword="null"/>.</param>
  /// <param name="error">What is wrong with the name, or <see langword="null"/>.</param>
  /// <returns>Whether the name is a profile.</returns>
  public static bool TryParse(string? name, out PythonProfile? profile, out string? error) {
    profile = null;
    error = null;
    if (name is null) {
      profile = Default;
      return true;
    }

    var version = name.StartsWith("py", StringComparison.Ordinal) ? name[2..] : name;
    profile = All.FirstOrDefault(candidate => candidate.Version == version);
    if (profile is not null)
      return true;

    error = $"unknown Python profile '{name}'; the profiles are {string.Join(", ", Names)}";
    return false;
  }

  /// <summary>Reads a profile name as <see cref="TryParse"/> does.</summary>
  /// <param name="name">The name; the default profile for <see langword="null"/>.</param>
  /// <returns>The profile.</returns>
  /// <exception cref="ArgumentException">The name is no profile.</exception>
  public static PythonProfile Parse(string? name)
    => TryParse(name, out var profile, out var error) ? profile! : throw new ArgumentException(error, nameof(name));

  /// <summary>The name of the profile.</summary>
  public override string ToString() => this.Name;

}
