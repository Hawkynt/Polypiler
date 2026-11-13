using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The scenarios of <c>tests/features/prototype/csharp-profiles.feature</c> and the test design of the
/// C# target profiles, with the real <c>polypiler</c> executable. A file it writes for a profile is
/// built twice by the C# compiler — Roslyn, in memory: in the language version of the profile against
/// the reference assemblies of the profile's framework, with every warning an error, which is what
/// says it is C# of that profile; and for the runtime the tests run on, with the project's overflow
/// checking off and on, to be called. The expected outcome of a call is never computed here: it is
/// what the original fixture does after the C# compiler built it into this assembly, what the
/// interpreter does with the imported IR, and for a JavaScript source what <c>node</c> does with it.
/// <para>
/// Every test here starts a process or the compiler, so the fixture is in the <c>EndToEnd</c> tier.
/// A fixture times nine profiles is a few hundred processes; the tests share nothing - each has its
/// own instance and its own working directory - and so run side by side.
/// </para>
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class CSharpProfilesTests {

  private static readonly string[] _scenarios = [
    "The same algorithm compiles for and runs like every profile",
    "A rotate is a library call where the library has it and a helper where it does not",
    "The namespace form follows the language version",
    "A helper is emitted only when used",
    "An unknown profile is a usage error",
    "The language version cannot be lowered below the profile's"
  ];

  private static readonly bool[] _overflowChecking = [false, true];

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(this._directory, "wide"));
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>The generated file without the header that names the profile: two profiles with the same code need one run.</summary>
  private static string Code(string text) => text[text.IndexOf("\n\n", StringComparison.Ordinal)..];

  /// <summary>Translates a file in the working directory with the real executable and returns the generated C#.</summary>
  private string Translate(string source, string language, string className, params string[] further) {
    var output = Path.Combine(this._directory, "out-" + Guid.NewGuid().ToString("N"));
    var (exitCode, text) = Toolchain.RunPolypiler(this._directory, ["translate", source, "--from", language, "--to", "csharp", "--output", output, .. further]);
    Assert.That(exitCode, Is.Zero, text);
    return File.ReadAllText(Path.Combine(output, className + ".cs"));
  }

  private string TranslateSnippet(string method, params string[] further) {
    File.WriteAllText(Path.Combine(this._directory, "Snippet.cs"), PrototypeFixture.Wrap(method));
    return this.Translate("Snippet.cs", "csharp", "Snippet", further);
  }

  /// <summary>Ensures the feature and its NUnit bindings stay in agreement.</summary>
  [Test]
  public void FeatureScenarios_HaveBindings() {
    var feature = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/features/prototype/csharp-profiles.feature"));
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario(?: Outline)?: (.+?)\r?$").Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
    var examples = Regex.Matches(feature, @"(?m)^      \| (\S+) +\| (\S+) +\|\r?$").Select(match => (match.Groups[1].Value, match.Groups[2].Value)).Skip(1);
    Assert.That(examples, Is.EqualTo(CSharpProfile.All.Select(profile => (profile.Name, profile.LanguageVersionName))));
  }

  /// <summary>Scenario outline: The same algorithm compiles for and runs like every profile.</summary>
  [TestCase("net2.0", "2")]
  [TestCase("net3.5", "3")]
  [TestCase("net4.6", "6")]
  [TestCase("net4.8", "7.3")]
  [TestCase("netstandard2.0", "7.3")]
  [TestCase("netstandard2.1", "8")]
  [TestCase("netcoreapp3.1", "8")]
  [TestCase("net8.0", "12")]
  [TestCase("net10.0", "14")]
  public void SameAlgorithm_CompilesForAndRunsLikeEveryProfile(string profile, string languageVersion) {
    File.Copy(PrototypeFixture.PathOf("wide/Crc32Table"), Path.Combine(this._directory, "wide", "Crc32Table.cs"));
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", "wide/Crc32Table.cs", "--from", "csharp", "--to", "csharp", "--profile", profile, "--output", "out");
    Assert.That(exitCode, Is.Zero, output);

    var generated = File.ReadAllText(Path.Combine(this._directory, "out", "Crc32Table.cs"));
    Assert.That(generated, Does.Contain($"\n// Profile: {profile}, C# {languageVersion}.\n"));
    Assert.That(GeneratedCSharp.ComplaintsFor(generated, profile, languageVersion), Is.Empty, generated);
    foreach (var checking in _overflowChecking)
      Assert.That(GeneratedCSharp.Compile(generated, checking).Method("Compute").Invoke(null, [PrototypeCases.Text("123456789")]), Is.EqualTo(3421780262u), $"CheckOverflow {checking}");
  }

  /// <summary>Scenario: A rotate is a library call where the library has it and a helper where it does not.</summary>
  [Test]
  public void Rotate_IsALibraryCallWhereTheLibraryHasIt_AndAHelperWhereItDoesNot() {
    const string method = "public static uint F(uint x, int n) => System.Numerics.BitOperations.RotateLeft(x, n);";
    var modern = this.TranslateSnippet(method, "--profile", "netcoreapp3.1");
    Assert.That(modern, Does.Contain("BitOperations.RotateLeft").And.Not.Contain("private static"));
    Assert.That(GeneratedCSharp.ComplaintsFor(modern, "netcoreapp3.1", "8"), Is.Empty, modern);

    var legacy = this.TranslateSnippet(method, "--profile", "net4.6");
    Assert.That(legacy, Does.Not.Contain("BitOperations").And.Contain("  private static uint RotateLeft(uint value, int count) {\n"));
    Assert.That(GeneratedCSharp.ComplaintsFor(legacy, "net4.6", "6"), Is.Empty, legacy);

    foreach (var text in new[] { modern, legacy })
      foreach (var checking in _overflowChecking)
        Assert.That(GeneratedCSharp.Compile(text, checking).Method("F").Invoke(null, [0x12345678u, 8]), Is.EqualTo(878082066u));
  }

  /// <summary>Scenario: The namespace form follows the language version.</summary>
  [Test]
  public void NamespaceForm_FollowsTheLanguageVersion() {
    File.Copy(PrototypeFixture.PathOf("Adler32"), Path.Combine(this._directory, "Adler32.cs"));
    var modern = this.Translate("Adler32.cs", "csharp", "Adler32", "--namespace", "Demo", "--profile", "net10.0");
    Assert.That(modern, Does.Contain("\nnamespace Demo;\n").And.Not.Contain("namespace Demo {"));
    Assert.That(GeneratedCSharp.ComplaintsFor(modern, "net10.0", "14"), Is.Empty, modern);

    var legacy = this.Translate("Adler32.cs", "csharp", "Adler32", "--namespace", "Demo", "--profile", "net4.8");
    Assert.That(legacy, Does.Contain("\nnamespace Demo {\n").And.Not.Contain("namespace Demo;"));
    Assert.That(GeneratedCSharp.ComplaintsFor(legacy, "net4.8", "7.3"), Is.Empty, legacy);

    foreach (var text in new[] { modern, legacy }) {
      var build = GeneratedCSharp.Compile(text, true);
      Assert.That(build.ClassName, Is.EqualTo("Demo.Adler32"));
      Assert.That(build.Method("Compute").Invoke(null, [PrototypeCases.Text("Wikipedia")]), Is.EqualTo(300286872u));
    }
  }

  /// <summary>Scenario: A helper is emitted only when used.</summary>
  [Test]
  public void Helper_IsEmittedOnlyWhenUsed() {
    File.Copy(PrototypeFixture.PathOf("Adler32"), Path.Combine(this._directory, "Adler32.cs"));
    var text = this.Translate("Adler32.cs", "csharp", "Adler32", "--profile", "net2.0");
    Assert.That(text, Does.Contain("  public static uint Compute(byte[] data) {\n").And.Not.Contain("private"));
    Assert.That(Regex.Matches(text, @"(?m)^  \w+ static "), Has.Count.EqualTo(1));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net2.0", "2"), Is.Empty, text);
  }

  /// <summary>Scenario: An unknown profile is a usage error.</summary>
  [Test]
  public void UnknownProfile_IsAUsageError() {
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", PrototypeFixture.PathOf("Adler32"), "--from", "csharp", "--to", "csharp", "--profile", "net1.1", "--output", "out");
    Assert.That(exitCode, Is.EqualTo(2), output);
    Assert.That(output, Does.Contain("error PLY0001: unknown C# profile 'net1.1'").And.Contain("usage: polypiler translate"));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
  }

  /// <summary>Scenario: The language version cannot be lowered below the profile's.</summary>
  [Test]
  public void LanguageVersion_CannotBeLoweredBelowTheProfiles() {
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", PrototypeFixture.PathOf("Adler32"), "--from", "csharp", "--to", "csharp", "--profile", "net10.0", "--langversion", "7.3", "--output", "out");
    Assert.That(exitCode, Is.EqualTo(2), output);
    Assert.That(output, Does.Contain("error PLY0001: the C# profile 'net10.0' is written in C# 14 or later, not in C# 7.3"));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
  }

  /// <summary>
  /// Test design 1, 2 and 12, with the executable: every C# fixture in every profile. Each file
  /// compiles against its profile's reference assemblies at its language version, and every call of
  /// the fixture — the published vectors, each operator and conversion at its boundaries and on a
  /// thousand random tuples (seed 2026) — has, without and with overflow checking, the outcome of the
  /// original fixture and of the interpreter.
  /// </summary>
  [TestCaseSource(typeof(PrototypeFixture), nameof(PrototypeFixture.Names))]
  public void EveryFixture_InEveryProfile_CompilesForItAndAgreesWithTheOriginal(string fixture) {
    var calls = PrototypeCases.For(fixture);
    var module = PrototypeFixture.Imported(fixture);
    var expected = calls.Select(call => PrototypeFixture.CompiledOutcome(fixture, call)).ToList();
    Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)), Is.EqualTo(expected), "the interpreter and the original fixture differ");

    var executed = new Dictionary<string, string>();
    var mismatches = new List<string>();
    var watch = Stopwatch.StartNew();
    var texts = CSharpProfile.All.ToDictionary(profile => profile, profile => this.Translate(PrototypeFixture.PathOf(fixture), "csharp", PrototypeFixture.ClassOf(fixture), "--profile", profile.Name));
    TestContext.Out.WriteLine($"{fixture}: translated for {texts.Count} profiles in {watch.ElapsedMilliseconds} ms");
    foreach (var (profile, text) in texts) {
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
      if (!executed.TryAdd(Code(text), profile.Name))
        continue;

      foreach (var checking in _overflowChecking) {
        var build = GeneratedCSharp.Compile(text, checking);
        for (var index = 0; index < calls.Count; ++index)
          if (build.Outcome(calls[index]) is var outcome && outcome != expected[index])
            mismatches.Add($"{profile.Name}, CheckOverflow {checking}: {calls[index]} is {outcome}, not {expected[index]}");
      }
    }

    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls x {executed.Count} distinct texts ({string.Join(", ", executed.Values)}) x 2 builds, {mismatches.Count} mismatches");
    Assert.That(calls, Is.Not.Empty);
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} outcomes differ");
  }

  /// <summary>
  /// Test design 5: every JavaScript fixture into every profile. Each file compiles against its
  /// profile's reference assemblies, and every call has the outcome <c>node</c> has for the original
  /// source. The file is given the name the class is to have, because a module is named after its
  /// file.
  /// </summary>
  [TestCaseSource(typeof(JavaScriptFixture), nameof(JavaScriptFixture.Names))]
  public void EveryJavaScriptFixture_InEveryProfile_AgreesWithNodeOnTheOriginal(string fixture) {
    var calls = JavaScriptCases.For(fixture).ToList();
    if (fixture == "operators")
      calls.AddRange(JavaScriptCases.RandomOperatorCalls());

    var className = char.ToUpperInvariant(fixture[0]) + fixture[1..];
    File.Copy(JavaScriptFixture.PathOf(fixture), Path.Combine(this._directory, className + ".mjs"));
    var expected = Toolchain.NodeOutcomes(this._directory, JavaScriptFixture.PathOf(fixture), calls);
    var executed = new Dictionary<string, string>();
    var mismatches = new List<string>();
    foreach (var profile in CSharpProfile.All) {
      var text = this.Translate(className + ".mjs", "javascript", className, "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
      if (!executed.TryAdd(Code(text), profile.Name))
        continue;

      foreach (var checking in _overflowChecking) {
        var build = GeneratedCSharp.Compile(text, checking);
        for (var index = 0; index < calls.Count; ++index)
          if (build.Outcome(calls[index]) is var outcome && outcome != expected[index])
            mismatches.Add($"{profile.Name}, CheckOverflow {checking}: {calls[index]} is {outcome}, node has {expected[index]}");
      }
    }

    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls x {executed.Count} distinct texts ({string.Join(", ", executed.Values)}) x 2 builds, {mismatches.Count} mismatches");
    Assert.That(calls, Is.Not.Empty);
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} outcomes differ");
  }

}
