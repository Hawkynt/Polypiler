using System;
using System.Collections.Concurrent;
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
[Layer("core")]
[Category("Prototype")]
public sealed class CSharpProfilesTests {

  private static readonly bool[] _overflowChecking = [false, true];

  /// <summary>The calls of a C# fixture with what the compiled fixture and the interpreter do for each, computed once for all profiles.</summary>
  private static readonly ConcurrentDictionary<string, Lazy<(IReadOnlyList<PrototypeCall> Calls, List<string> Compiled, List<string> Interpreted)>> _expected = new(StringComparer.Ordinal);

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
  private string Translate(string source, string language, string className, params string[] further)
    => this.Translate(false, source, language, className, further);

  /// <summary>Translates a file in the working directory in process or with the real executable and returns the generated C#.</summary>
  private string Translate(bool inProcess, string source, string language, string className, params string[] further) {
    var output = Path.Combine(this._directory, "out-" + Guid.NewGuid().ToString("N"));
    var (exitCode, text) = Toolchain.Polypiler(inProcess, this._directory, ["translate", source, "--from", language, "--to", "csharp", "--output", output, .. further]);
    Assert.That(exitCode, Is.Zero, text);
    return File.ReadAllText(Path.Combine(output, className + ".cs"));
  }

  /// <summary>The examples of the scenario outline are every profile, each with its language version.</summary>
  [Test]
  public void FeatureExamples_AreEveryProfile() {
    var feature = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/features/prototype/csharp-profiles.feature"));
    var examples = Regex.Matches(feature, @"(?m)^      \| (\S+) +\| (\S+) +\|\r?$").Select(match => (match.Groups[1].Value, match.Groups[2].Value)).Skip(1);
    Assert.That(examples, Is.EqualTo(CSharpProfile.All.Select(profile => (profile.Name, profile.LanguageVersionName))));
  }

  /// <summary>Scenario: An unknown profile is a usage error.</summary>
  [Test]
  public void UnknownProfile_IsAUsageError() {
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", PrototypeFixture.PathOf("Adler32"), "--from", "csharp", "--to", "csharp", "--profile", "net1.1", "--output", "out");
    Assert.That(exitCode, Is.EqualTo(2), output);
    Assert.That(output, Does.Contain("polypiler: error PLY0003: unknown C# profile 'net1.1'").And.Not.Contain("usage:"));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
  }

  /// <summary>Scenario: The language version cannot be lowered below the profile's.</summary>
  [Test]
  public void LanguageVersion_CannotBeLoweredBelowTheProfiles() {
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", PrototypeFixture.PathOf("Adler32"), "--from", "csharp", "--to", "csharp", "--profile", "net10.0", "--langversion", "7.3", "--output", "out");
    Assert.That(exitCode, Is.EqualTo(2), output);
    Assert.That(output, Does.Contain("error PLY0003: the C# profile 'net10.0' is written in C# 14 or later, not in C# 7.3"));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
  }

  private static IEnumerable<TestCaseData> FixturesAndProfiles()
    => from fixture in PrototypeFixture.Names from profile in CSharpProfile.All select new TestCaseData(fixture, profile.Name);

  private static IEnumerable<TestCaseData> ScriptsAndProfiles()
    => from fixture in JavaScriptFixture.Names from profile in CSharpProfile.All select new TestCaseData(fixture, profile.Name);

  /// <summary>
  /// The earlier profile whose file has the same code as that of <paramref name="profile"/> and is run
  /// in its place, or <see langword="null"/> when the code of the profile is run by its own case.
  /// </summary>
  private static CSharpProfile? RunBy(CSharpProfile profile, Func<CSharpProfile, string> translate) {
    var code = Code(translate(profile));
    return CSharpProfile.All.TakeWhile(candidate => candidate != profile).FirstOrDefault(candidate => Code(translate(candidate)) == code);
  }

  /// <summary>
  /// Test design 1, 2 and 12, with the command's own entry point: every C# fixture in every profile.
  /// Each file compiles against its profile's reference assemblies at its language version, and every
  /// call of the fixture — the published vectors, each operator and conversion at its boundaries and on
  /// a thousand random tuples (seed 2026) — has, without and with overflow checking, the outcome of the
  /// original fixture and of the interpreter. A profile whose code is that of an earlier profile is
  /// compiled for its framework, and not run again.
  /// </summary>
  [TestCaseSource(nameof(FixturesAndProfiles))]
  public void EveryFixture_InEveryProfile_CompilesForItAndAgreesWithTheOriginal(string fixture, string name) {
    var (calls, expected, interpreted) = _expected.GetOrAdd(fixture, key => new(() => {
      var all = PrototypeCases.For(key);
      var module = PrototypeFixture.Imported(key);
      return (all, [.. all.Select(call => PrototypeFixture.CompiledOutcome(key, call))], [.. all.Select(call => PrototypeFixture.InterpretedOutcome(module, call))]);
    })).Value;
    Assert.That(interpreted, Is.EqualTo(expected), "the interpreter and the original fixture differ");

    var profile = CSharpProfile.All.Single(candidate => candidate.Name == name);
    string Translated(CSharpProfile target) => this.Translate(true, PrototypeFixture.PathOf(fixture), "csharp", PrototypeFixture.ClassOf(fixture), "--profile", target.Name);
    var watch = Stopwatch.StartNew();
    var text = Translated(profile);
    TestContext.Out.WriteLine($"{fixture}: translated for {profile.Name} in {watch.ElapsedMilliseconds} ms");
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
    if (RunBy(profile, Translated) is { } earlier) {
      TestContext.Out.WriteLine($"{fixture}: {profile.Name} has the code of {earlier.Name}, which runs it");
      return;
    }

    var mismatches = new List<string>();
    foreach (var checking in _overflowChecking) {
      var build = GeneratedCSharp.Compile(text, checking);
      for (var index = 0; index < calls.Count; ++index)
        if (build.Outcome(calls[index]) is var outcome && outcome != expected[index])
          mismatches.Add($"{profile.Name}, CheckOverflow {checking}: {calls[index]} is {outcome}, not {expected[index]}");
    }

    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls x 2 builds of {profile.Name}, {mismatches.Count} mismatches");
    Assert.That(calls, Is.Not.Empty);
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} outcomes differ");
  }

  /// <summary>
  /// Test design 5: every JavaScript fixture into every profile. Each file compiles against its
  /// profile's reference assemblies, and every call has the outcome <c>node</c> has for the original
  /// source. The file is given the name the class is to have, because a module is named after its
  /// file. A profile whose code is that of an earlier profile is compiled for its framework, and not
  /// run again.
  /// </summary>
  [TestCaseSource(nameof(ScriptsAndProfiles))]
  public void EveryJavaScriptFixture_InEveryProfile_AgreesWithNodeOnTheOriginal(string fixture, string profileName) {
    var calls = JavaScriptCases.For(fixture).ToList();
    if (fixture == "operators")
      calls.AddRange(JavaScriptCases.RandomOperatorCalls());

    // The class is named after the file, without the folder of the fixture.
    var name = Path.GetFileName(fixture);
    var className = char.ToUpperInvariant(name[0]) + name[1..];
    File.Copy(JavaScriptFixture.PathOf(fixture), Path.Combine(this._directory, className + ".mjs"));

    // A module that computes in doubles has two zeros, so -0 is told from 0.
    var expected = Toolchain.NodeOutcomes(this._directory, JavaScriptFixture.PathOf(fixture), calls, true);
    var profile = CSharpProfile.All.Single(candidate => candidate.Name == profileName);
    string Translated(CSharpProfile target) => this.Translate(true, className + ".mjs", "javascript", className, "--profile", target.Name);
    var text = Translated(profile);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
    if (RunBy(profile, Translated) is { } earlier) {
      TestContext.Out.WriteLine($"{fixture}: {profile.Name} has the code of {earlier.Name}, which runs it");
      return;
    }

    var mismatches = new List<string>();
    foreach (var checking in _overflowChecking) {
      var build = GeneratedCSharp.Compile(text, checking);
      for (var index = 0; index < calls.Count; ++index)
        if (build.Outcome(calls[index]) is var outcome && outcome != expected[index])
          mismatches.Add($"{profile.Name}, CheckOverflow {checking}: {calls[index]} is {outcome}, node has {expected[index]}");
    }

    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls x 2 builds of {profile.Name}, {mismatches.Count} mismatches");
    Assert.That(calls, Is.Not.Empty);
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} outcomes differ");
  }

}
