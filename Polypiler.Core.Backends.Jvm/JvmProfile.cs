using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.Jvm;

/// <summary>The output packaging format produced by <see cref="JvmEmitter"/>.</summary>
public enum JvmPackageFormat {

  /// <summary>A deterministic JAR archive (<c>&lt;Module&gt;.jar</c>) containing <c>META-INF/MANIFEST.MF</c> and compiled <c>.class</c> files.</summary>
  Jar,

  /// <summary>Individual JVMS <c>.class</c> files emitted directly without JAR packaging.</summary>
  Class

}

/// <summary>
/// A JVM bytecode target profile selecting the JVMS classfile major version (<c>jvm/8</c>, <c>jvm/17</c>,
/// or <c>jvm/21</c>) and default output package format (<see cref="JvmPackageFormat.Jar"/> or <see cref="JvmPackageFormat.Class"/>).
/// </summary>
/// <param name="Name">The canonical profile name (<c>jvm/21</c>, <c>jvm/17</c>, or <c>jvm/8</c>).</param>
/// <param name="MajorVersion">The JVMS classfile major version (52 for Java 8, 61 for Java 17, 65 for Java 21).</param>
/// <param name="PackageFormat">The default package format when not overridden by <see cref="JvmEmitOptions.PackageFormat"/>.</param>
public sealed record JvmProfile(string Name, ushort MajorVersion, JvmPackageFormat PackageFormat = JvmPackageFormat.Jar) {

  /// <summary>The Java 21 LTS profile (<c>jvm/21</c>, classfile version 65.0).</summary>
  public static JvmProfile Java21 { get; } = new("jvm/21", 65);

  /// <summary>The Java 17 LTS profile (<c>jvm/17</c>, classfile version 61.0).</summary>
  public static JvmProfile Java17 { get; } = new("jvm/17", 61);

  /// <summary>The Java 8 LTS profile (<c>jvm/8</c>, classfile version 52.0).</summary>
  public static JvmProfile Java8 { get; } = new("jvm/8", 52);

  /// <summary>The default JVM profile (<see cref="Java21"/>).</summary>
  public static JvmProfile Default => Java21;

  /// <summary>Every supported JVM profile name.</summary>
  public static IReadOnlyList<string> Names { get; } = ["jvm/21", "jvm/17", "jvm/8"];

  /// <summary>Every supported package format name.</summary>
  public static IReadOnlyList<string> PackageFormatNames { get; } = ["jar", "class"];

  /// <summary>Parses a JVM profile name and optional package format.</summary>
  /// <param name="profile">The profile name (<c>jvm/21</c>, <c>jvm/17</c>, <c>jvm/8</c>, or <see langword="null"/> for default).</param>
  /// <param name="packageFormat">The package format (<c>jar</c>, <c>class</c>, or <see langword="null"/> for default).</param>
  /// <returns>The parsed <see cref="JvmProfile"/>.</returns>
  /// <exception cref="ArgumentException">The profile or package format is not recognized.</exception>
  public static JvmProfile Parse(string? profile = null, string? packageFormat = null)
    => TryParse(profile, packageFormat, out var result, out var error) ? result! : throw new ArgumentException(error);

  /// <summary>Attempts to parse a JVM profile name and optional package format.</summary>
  /// <param name="profile">The profile name, or <see langword="null"/> for default.</param>
  /// <param name="packageFormat">The package format, or <see langword="null"/> for default.</param>
  /// <param name="result">The parsed profile when valid; otherwise <see langword="null"/>.</param>
  /// <param name="error">A description of what was invalid; otherwise <see langword="null"/>.</param>
  /// <returns><see langword="true"/> when both inputs are valid.</returns>
  public static bool TryParse(string? profile, string? packageFormat, out JvmProfile? result, out string? error) {
    result = null;
    error = null;
    var baseProfile = profile switch {
      null or "jvm/21" => Java21,
      "jvm/17" => Java17,
      "jvm/8" => Java8,
      _ => null
    };
    if (baseProfile is null) {
      error = $"unknown JVM profile '{profile}'; the profiles are jvm/21, jvm/17 and jvm/8";
      return false;
    }

    var format = packageFormat switch {
      null or "jar" => JvmPackageFormat.Jar,
      "class" => JvmPackageFormat.Class,
      _ => (JvmPackageFormat)(-1)
    };
    if ((int)format < 0) {
      error = $"unknown JVM package format '{packageFormat}'; the formats are jar and class";
      return false;
    }

    result = baseProfile with { PackageFormat = format };
    return true;
  }

  /// <inheritdoc/>
  public override string ToString() => this.Name;

}

/// <summary>Options controlling packaging, Java package namespace, and entry point manifest metadata.</summary>
public sealed record JvmEmitOptions {

  /// <summary>Default emission options.</summary>
  public static JvmEmitOptions Default { get; } = new();

  /// <summary>Overrides the profile's <see cref="JvmProfile.PackageFormat"/> when set.</summary>
  public JvmPackageFormat? PackageFormat { get; init; }

  /// <summary>Optional dotted Java package name (e.g. <c>com.example.math</c>); <see langword="null"/> places the class in the default package.</summary>
  public string? PackageName { get; init; }

  /// <summary>Optional explicit <c>Main-Class</c> manifest attribute for JAR packaging.</summary>
  public string? MainClass { get; init; }

}
