using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.CSharp;

/// <summary>
/// Every fixture in every profile. Two things are asked of each generated file, and neither is
/// answered by reading it. The C# compiler — Roslyn, in memory — builds it in the language version
/// of the profile against the reference assemblies of the profile's framework, with every warning an
/// error: that is what says the file is C# 2 for .NET Framework 2.0. And the compiler builds it for
/// the runtime the tests run on, with the project's overflow checking off and on, and every call of
/// the fixture is made: the outcome has to be the one of the original fixture as the C# compiler
/// built it into this assembly, and the one of the interpreter.
/// </summary>
[TestFixture]
public sealed class CSharpProfileDifferentialTests {

  private static readonly bool[] _overflowChecking = [false, true];

  /// <summary>Every fixture in every profile. Each is compiled for the profile and again for every distinct text, which is quick in kind and not in practice.</summary>
  private static IEnumerable<TestCaseData> Fixtures()
    => from name in PrototypeFixture.Names from profile in CSharpProfile.All select new TestCaseData(name, profile.Name).SetCategory("Slow");

  private static IEnumerable<TestCaseData> Scripts()
    => from name in JavaScriptFixture.Names from profile in CSharpProfile.All select new TestCaseData(name, profile.Name);

  /// <summary>The calls of a C# fixture with what the compiled fixture and the interpreter do for each, computed once for all profiles.</summary>
  private static readonly ConcurrentDictionary<string, Lazy<(IReadOnlyList<PrototypeCall> Calls, List<string> Compiled, List<string> Interpreted)>> _expected = new(StringComparer.Ordinal);

  /// <summary>
  /// The earlier profile whose file has the same code as that of <paramref name="profile"/> and runs
  /// in its place, or <see langword="null"/> when the case of the profile runs its code.
  /// </summary>
  private static CSharpProfile? RunBy(IrModule module, CSharpProfile profile) {
    var code = Code(CSharpEmitter.Emit(module, profile).Single().Content);
    return CSharpProfile.All.TakeWhile(candidate => candidate != profile).FirstOrDefault(candidate => Code(CSharpEmitter.Emit(module, candidate).Single().Content) == code);
  }

  /// <summary>The generated file without the header that names the profile: two profiles with the same code need one run.</summary>
  private static string Code(string text) => text[text.IndexOf("\n\n", StringComparison.Ordinal)..];

  /// <summary>
  /// Test design 1, 2 and 12: every fixture, in every profile, compiles against that profile's
  /// reference assemblies at its language version and has, executed, the outcomes of the original
  /// and of the interpreter - on the published vectors, the boundary combinations and the random
  /// tuples alike, without and with overflow checking. A profile whose code is that of an earlier
  /// profile is compiled for its framework, and not run again.
  /// </summary>
  [TestCaseSource(nameof(Fixtures))]
  public void EveryProfile_CompilesForItsFramework_AndComputesWhatTheOriginalDoes(string fixture, string name) {
    var module = PrototypeFixture.Imported(fixture);
    var (calls, expected, interpreted) = _expected.GetOrAdd(fixture, key => new(() => {
      var all = PrototypeCases.For(key);
      return (all, [.. all.Select(call => PrototypeFixture.CompiledOutcome(key, call))], [.. all.Select(call => PrototypeFixture.InterpretedOutcome(module, call))]);
    })).Value;
    Assert.That(interpreted, Is.EqualTo(expected), "the interpreter and the original fixture differ");

    var profile = CSharpProfile.All.Single(candidate => candidate.Name == name);
    var mismatches = new List<string>();
    var text = CSharpEmitter.Emit(module, profile).Single().Content;
    Assert.That(text, Does.Contain($"\n// Profile: {profile.Name}, C# {profile.LanguageVersionName}.\n"));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
    if (RunBy(module, profile) is { } earlier) {
      TestContext.Out.WriteLine($"{fixture}: {profile.Name} has the code of {earlier.Name}, which runs it");
      return;
    }

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
  /// Test design 5: every JavaScript fixture, in every profile, compiles against that profile's
  /// reference assemblies and has, executed, the outcomes node recorded for the original source.
  /// The acceptance tests run node again. The module is given the name the class is to have: a
  /// module is named after its file, and a function named like its class would have to give way. A
  /// profile whose code is that of an earlier profile is compiled for its framework, and not run again.
  /// </summary>
  [TestCaseSource(nameof(Scripts))]
  public void EveryProfile_ComputesWhatNodeDoesWithTheJavaScriptOriginal(string fixture, string name) {
    var module = JavaScriptFixture.Import(char.ToUpperInvariant(fixture[0]) + fixture[1..] + ".mjs", JavaScriptFixture.SourceOf(fixture));
    var calls = JavaScriptCases.For(fixture);
    var recorded = JavaScriptFixture.RecordedLines(fixture);
    Assert.That(recorded, Has.Length.EqualTo(calls.Count), "the recording does not belong to these calls");

    var profile = CSharpProfile.All.Single(candidate => candidate.Name == name);
    var mismatches = new List<string>();
    var text = CSharpEmitter.Emit(module, profile).Single().Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
    if (RunBy(module, profile) is { } earlier) {
      TestContext.Out.WriteLine($"{fixture}: {profile.Name} has the code of {earlier.Name}, which runs it");
      return;
    }

    foreach (var checking in _overflowChecking) {
      var build = GeneratedCSharp.Compile(text, checking);
      for (var index = 0; index < calls.Count; ++index)
        if (JavaScriptFixture.Line(calls[index], build.Outcome(calls[index])) is var line && line != recorded[index])
          mismatches.Add($"{profile.Name}, CheckOverflow {checking}: {line}, node {recorded[index]}");
    }

    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls x 2 builds of {profile.Name}, {mismatches.Count} mismatches");
    Assert.That(calls, Is.Not.Empty);
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} outcomes differ");
  }

  /// <summary>
  /// Test design 11: a profile whose reference assemblies are not restored is not compiled against
  /// anything else - the lookup fails, and says which package is missing.
  /// </summary>
  [TestCase("net2.0", "Microsoft.NETFramework.ReferenceAssemblies.net20 1.0.3")]
  [TestCase("net3.5", "Microsoft.NETFramework.ReferenceAssemblies.net35 1.0.3")]
  [TestCase("net4.6", "Microsoft.NETFramework.ReferenceAssemblies.net46 1.0.3")]
  [TestCase("net4.8", "Microsoft.NETFramework.ReferenceAssemblies.net48 1.0.3")]
  [TestCase("netstandard2.0", "NETStandard.Library 2.0.3")]
  [TestCase("netstandard2.1", "NETStandard.Library.Ref 2.1.0")]
  [TestCase("netcoreapp3.1", "Microsoft.NETCore.App.Ref 3.1.0")]
  [TestCase("net8.0", "Microsoft.NETCore.App.Ref 8.0.0")]
  [TestCase("net10.0", "Microsoft.NETCore.App.Ref 10.0.0")]
  public void MissingReferenceAssemblies_FailAndNameThePackage(string profile, string package) {
    var empty = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(empty);
    try {
      Assert.That(() => ReferenceAssemblies.Of(profile, empty),
        Throws.InvalidOperationException.With.Message.Contains($"'{profile}'").And.Message.Contains($"the package {package} is not restored"));
    } finally {
      Directory.Delete(empty, true);
    }

    Assert.That(ReferenceAssemblies.Of(profile, ReferenceAssemblies.PackageRoot), Is.Not.Empty);
  }

  /// <summary>Every profile of the backend has reference assemblies to be compiled against, and no other name has.</summary>
  [Test]
  public void EveryProfile_HasReferenceAssemblies()
    => Assert.That(ReferenceAssemblies.Profiles, Is.EquivalentTo(CSharpProfile.All.Select(profile => profile.Name)));

  /// <summary>A language version the compiler does not know is an error of the test, not a compilation at some other version.</summary>
  [Test]
  public void UnknownLanguageVersion_IsNotCompiled()
    => Assert.That(() => GeneratedCSharp.ComplaintsFor("class C { }", "net10.0", "99"), Throws.InvalidOperationException.With.Message.Contains("'99'"));

  /// <summary>
  /// The check can fail: code that needs a newer language or a newer framework than the profile it
  /// is compiled for is reported, each by the compiler's own diagnostic.
  /// </summary>
  [TestCase("net2.0", "2", "static class C { static int F(int x) { var y = x; return y; } }", "CS8023")]
  [TestCase("net4.8", "7.3", "static class C { static float F(float x) { return System.MathF.Sqrt(x); } }", "CS0234")]
  [TestCase("netstandard2.1", "8", "static class C { static int F(uint x) { return System.Numerics.BitOperations.PopCount(x); } }", "CS0234")]
  [TestCase("net8.0", "12", "static class C { static int F(int x) { int unused; return x; } }", "CS0168")]
  public void CodeTheProfileDoesNotHave_IsReported(string profile, string languageVersion, string source, string diagnostic)
    => Assert.That(GeneratedCSharp.ComplaintsFor(source, profile, languageVersion), Has.Some.Contains(diagnostic));

}
