using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>The language a native source is written in.</summary>
public enum NativeLanguage {

  /// <summary>C, built as C17.</summary>
  C,

  /// <summary>C++, built as C++20.</summary>
  Cpp

}

/// <summary>
/// How the native layer compiles C and C++ (<c>tooling/ci/layers/native.yaml</c>): the language
/// standard, every warning an error, and the address and undefined-behaviour sanitizers wherever
/// <c>tooling/ci/layers/native.sanitizers.yaml</c> says the platform and compiler have them, with no
/// recovery, so that a finding ends the program. The compilers are the toolchain keys <c>gcc</c> and
/// <c>clang</c>. Whatever builds generated C or C++ reuses <see cref="Sanitized(NativeLanguage, string)"/>.
/// </summary>
public static class NativeFlags {

  /// <summary>The sanitizer table, relative to the repository root.</summary>
  public const string SanitizerTable = "tooling/ci/layers/native.sanitizers.yaml";

  /// <summary>The value of the table for a combination without sanitizers.</summary>
  public const string Unavailable = "none";

  private static readonly Lazy<IReadOnlyDictionary<string, string>> _table = new(() => ReadTable(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, SanitizerTable))));

  /// <summary>The switches every build has: all warnings as errors, light optimization, debug information and frame pointers for the sanitizers' stack traces.</summary>
  public static IReadOnlyList<string> Common { get; } = ["-Wall", "-Wextra", "-Werror", "-O1", "-g", "-fno-omit-frame-pointer"];

  /// <summary>The table: <c>&lt;platform&gt;/&lt;compiler&gt;</c> to its <c>-fsanitize=</c> groups or <see cref="Unavailable"/>.</summary>
  public static IReadOnlyDictionary<string, string> Sanitizers => _table.Value;

  /// <summary>The switch of the language standard.</summary>
  /// <param name="language">The language.</param>
  public static string Standard(NativeLanguage language) => language == NativeLanguage.C ? "-std=c17" : "-std=c++20";

  /// <summary>The sanitizer groups a compiler has on a platform; <see langword="null"/> where the table says <see cref="Unavailable"/>.</summary>
  /// <param name="compiler">The toolchain key of the compiler.</param>
  /// <param name="platform">The platform as the toolchain lock names it: <c>linux</c>, <c>macos</c> or <c>windows</c>.</param>
  /// <exception cref="InvalidOperationException">The table has no row for the combination: a build must never fall back to no sanitizers silently.</exception>
  public static string? SanitizersOf(string compiler, string platform)
    => Sanitizers.TryGetValue($"{platform}/{compiler}", out var groups)
      ? groups == Unavailable ? null : groups
      : throw new InvalidOperationException($"{SanitizerTable} has no row '{platform}/{compiler}'");

  /// <summary>The flags of a build on this platform.</summary>
  /// <param name="language">The language.</param>
  /// <param name="compiler">The toolchain key of the compiler.</param>
  public static IReadOnlyList<string> Sanitized(NativeLanguage language, string compiler) => Sanitized(language, compiler, ToolchainLocator.CurrentPlatform);

  /// <summary>The flags of a build: the standard, <see cref="Common"/>, and the sanitizers with no recovery where the platform has them.</summary>
  /// <param name="language">The language.</param>
  /// <param name="compiler">The toolchain key of the compiler.</param>
  /// <param name="platform">The platform as the toolchain lock names it.</param>
  public static IReadOnlyList<string> Sanitized(NativeLanguage language, string compiler, string platform)
    => SanitizersOf(compiler, platform) is { } groups
      ? [Standard(language), .. Common, $"-fsanitize={groups}", "-fno-sanitize-recover=all"]
      : [Standard(language), .. Common];

  /// <summary>The name a program gets on this platform: with <c>.exe</c> on Windows.</summary>
  /// <param name="name">The name without extension.</param>
  public static string Program(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

  /// <summary>
  /// The build step that compiles one source of the run's directory into a program there. C++ is
  /// compiled and linked as C++: clang by its C++ driver mode, gcc with its C++ library named, since
  /// the toolchain lock carries the C drivers. On macOS the step gets the SDK the CI exports as
  /// <c>SDKROOT</c>, which a compiler from outside Xcode needs to find the system headers.
  /// </summary>
  /// <param name="compiler">The toolchain key of the compiler.</param>
  /// <param name="language">The language.</param>
  /// <param name="source">The source, relative to the run's directory.</param>
  /// <param name="program">The program, relative to the run's directory.</param>
  /// <param name="defines">Further switches, before the source.</param>
  public static ProcessSpec Compile(string compiler, NativeLanguage language, string source, string program, params IReadOnlyList<string> defines) {
    var cpp = language == NativeLanguage.Cpp;
    var arguments = new List<string>();
    if (cpp && compiler == "clang")
      arguments.Add("--driver-mode=g++");
    arguments.AddRange(Sanitized(language, compiler));
    arguments.AddRange(defines);
    arguments.AddRange([source, "-o", program]);
    if (cpp && compiler == "gcc")
      arguments.Add("-lstdc++");

    var environment = new Dictionary<string, string>(StringComparer.Ordinal);
    if (OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("SDKROOT") is { Length: > 0 } sdk)
      environment["SDKROOT"] = sdk;
    return new ProcessSpec(compiler, arguments) { Environment = environment };
  }

  /// <summary>Reads the sanitizer table.</summary>
  /// <param name="text">The YAML text.</param>
  /// <exception cref="InvalidDataException">The text is not schema 1 with a mapping <c>sanitizers</c> of strings.</exception>
  public static IReadOnlyDictionary<string, string> ReadTable(string text) {
    var document = YamlToJson.Read(text, out _) as JsonObject;
    if (document?["schema"]?.ToString() != "1" || document["sanitizers"] is not JsonObject rows)
      throw new InvalidDataException($"{SanitizerTable}: expected 'schema: 1' and a mapping 'sanitizers'");

    return rows.ToDictionary(row => row.Key, row => row.Value is JsonValue value && value.ToString() is { Length: > 0 } groups ? groups : throw new InvalidDataException($"{SanitizerTable}: '{row.Key}' is not a list of sanitizers or '{Unavailable}'"), StringComparer.Ordinal);
  }

}
