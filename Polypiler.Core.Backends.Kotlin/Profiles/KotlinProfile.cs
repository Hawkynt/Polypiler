using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Polypiler.Core.Backends.Kotlin;

/// <summary>
/// The Kotlin a module is written for: a language version of Kotlin for the JVM, <c>kotlin2.0</c> to
/// <c>kotlin2.4</c> - the versions the pinned compiler (<c>tooling/toolchains.lock</c>, kotlinc 2.4.21)
/// accepts as <c>-language-version</c> - and the JVM release the class files run on
/// (<c>-jvm-target</c>: 1.8, 11, 17 or 21). What each pair has is the <see cref="KotlinCapabilities"/>;
/// the output compiles with the language version named and runs on the JVM named and every later one.
/// </summary>
public sealed record KotlinProfile {

  /// <summary>The language versions a profile names, oldest first: the minor versions of Kotlin 2.</summary>
  public static IReadOnlyList<int> Minors { get; } = [0, 1, 2, 3, 4];

  /// <summary>The JVM releases a profile can target, oldest first, as <c>-jvm-target</c> spells them.</summary>
  public static IReadOnlyList<string> JvmTargets { get; } = ["1.8", "11", "17", "21"];

  private KotlinProfile(int minor, string jvmTarget) {
    this.Minor = minor;
    this.JvmTarget = jvmTarget;
  }

  /// <summary>The minor version of Kotlin 2, 0 to 4.</summary>
  public int Minor { get; }

  /// <summary>The JVM release the class files target, as <c>-jvm-target</c> spells it.</summary>
  public string JvmTarget { get; }

  /// <summary>The JVM release as a number: 8 for <c>1.8</c>, otherwise the release.</summary>
  public int Jvm => this.JvmTarget == "1.8" ? 8 : int.Parse(this.JvmTarget, CultureInfo.InvariantCulture);

  /// <summary>The language version as <c>-language-version</c> takes it, <c>2.&lt;minor&gt;</c>.</summary>
  public string Version => "2." + this.Minor.ToString(CultureInfo.InvariantCulture);

  /// <summary>The name of the profile: <c>kotlin2.&lt;minor&gt;</c>.</summary>
  public string Name => "kotlin" + this.Version;

  /// <summary>Every language version on the default JVM target, oldest first.</summary>
  public static IReadOnlyList<KotlinProfile> All { get; } = [.. Minors.Select(minor => new KotlinProfile(minor, JvmTargets[0]))];

  /// <summary>Every language version on every JVM target, oldest first.</summary>
  public static IReadOnlyList<KotlinProfile> Everything { get; } = [.. All.SelectMany(profile => JvmTargets.Select(profile.WithJvmTarget))];

  /// <summary>The names of every profile, oldest first.</summary>
  public static IReadOnlyList<string> Names { get; } = [.. All.Select(profile => profile.Name)];

  /// <summary>
  /// The profile a translation writes when none is named: the newest language version of the pinned
  /// compiler, on Java 8, which is Kotlin's own default target.
  /// </summary>
  public static KotlinProfile Default { get; } = All[^1];

  /// <summary>The same language version for another JVM target.</summary>
  /// <param name="jvmTarget">The target, one of <see cref="JvmTargets"/>.</param>
  /// <returns>The profile.</returns>
  /// <exception cref="ArgumentException">The target is not one of <see cref="JvmTargets"/>.</exception>
  public KotlinProfile WithJvmTarget(string jvmTarget) => JvmTargets.Contains(jvmTarget)
    ? new KotlinProfile(this.Minor, jvmTarget)
    : throw new ArgumentException($"unknown JVM target '{jvmTarget}'; the targets are {string.Join(", ", JvmTargets)}", nameof(jvmTarget));

  /// <summary>
  /// Reads a profile: <c>kotlin2.0</c> to <c>kotlin2.4</c>, the bare version <c>2.0</c> to <c>2.4</c>,
  /// or <c>kotlin/jvm</c> for the default; a JVM target follows after <c>-jvm</c>, as in
  /// <c>kotlin2.2-jvm17</c>.
  /// </summary>
  /// <param name="name">The name; the default profile for <see langword="null"/>.</param>
  /// <param name="profile">The profile, or <see langword="null"/>.</param>
  /// <param name="error">What is wrong with the name, or <see langword="null"/>.</param>
  /// <returns>Whether the name is a profile.</returns>
  public static bool TryParse(string? name, out KotlinProfile? profile, out string? error) {
    profile = null;
    error = null;
    if (name is null || name == "kotlin/jvm") {
      profile = Default;
      return true;
    }

    var (version, jvm) = name.IndexOf("-jvm", StringComparison.Ordinal) is var at and >= 0 ? (name[..at], name[(at + 4)..]) : (name, JvmTargets[0]);
    if (jvm == "8")
      jvm = "1.8";

    version = version.StartsWith("kotlin", StringComparison.Ordinal) ? version[6..] : version;
    var language = All.FirstOrDefault(candidate => candidate.Version == version);
    if (language is not null && JvmTargets.Contains(jvm)) {
      profile = language.WithJvmTarget(jvm);
      return true;
    }

    error = $"unknown Kotlin profile '{name}'; the profiles are {string.Join(", ", Names)}, each optionally with -jvm<{string.Join("|", JvmTargets)}>";
    return false;
  }

  /// <summary>Reads a profile name as <see cref="TryParse"/> does.</summary>
  /// <param name="name">The name; the default profile for <see langword="null"/>.</param>
  /// <returns>The profile.</returns>
  /// <exception cref="ArgumentException">The name is no profile.</exception>
  public static KotlinProfile Parse(string? name)
    => TryParse(name, out var profile, out var error) ? profile! : throw new ArgumentException(error, nameof(name));

  /// <summary>
  /// The options of <c>kotlinc</c> that compile for this profile: the language and API version, the
  /// JVM target, and the suppression of the warning a deprecated language version gives, so that a
  /// build with warnings as errors accepts every profile.
  /// </summary>
  public IReadOnlyList<string> CompilerOptions => ["-language-version", this.Version, "-api-version", this.Version, "-jvm-target", this.JvmTarget, "-Xsuppress-version-warnings"];

  /// <summary>The name of the profile, with the JVM target where it is not the default.</summary>
  public override string ToString() => this.JvmTarget == JvmTargets[0] ? this.Name : $"{this.Name}-jvm{(this.Jvm).ToString(CultureInfo.InvariantCulture)}";

}
