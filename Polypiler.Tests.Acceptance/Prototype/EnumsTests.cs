using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// Test design 1, 2, 3, 4 and 6 of #1318 with <c>node</c>: a module of every operation on enums over
/// every underlying type, written at every level, module format and typed-array setting, computes for
/// every call what the interpreter computes - the text of every flags value among them, the boundaries
/// of every underlying type and every class of text a parse distinguishes.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("core")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class EnumsTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Every operation at every level runs in node like the interpreter.</summary>
  [TestCaseSource(typeof(ProfileCase), nameof(ProfileCase.All))]
  public void EveryOperation_RunsInNodeLikeTheInterpreter(ProfileCase profile) {
    var module = EnumFixtures.Module;
    var parsed = profile.Parsed;
    Assert.That(LegalizedJavaScript.Check(module, parsed), Is.Empty);
    var file = module.Name + parsed.Extension;
    var text = LegalizedJavaScript.Emit(module, parsed).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, parsed), Is.Empty, text);
    File.WriteAllText(Path.Combine(this._directory, file), text);
    var calls = EnumFixtures.Calls().ToList();
    var outcomes = Toolchain.NodeOutcomes(this._directory, file, calls, false, profile.Mode, profile.Global);
    var mismatches = calls.Select((call, index) => (call, Expected: PrototypeFixture.InterpretedOutcome(module, call), Actual: outcomes[index]))
      .Where(result => result.Expected != result.Actual).Select(result => $"{result.call}: {result.Actual}, the interpreter {result.Expected}").ToList();
    Assert.That(mismatches.Take(10), Is.Empty, $"{mismatches.Count} of {calls.Count} in {profile}\n{text}");
  }

  /// <summary>Translates a file with the real executable; returns the generated text.</summary>
  private string Translate(string source, string from, string to, string generated, params string[] further) {
    var output = Path.Combine(this._directory, "out");
    var (exitCode, text) = Toolchain.Polypiler(true, this._directory, ["translate", source, "--from", from, "--to", to, "--output", output, .. further]);
    Assert.That(exitCode, Is.Zero, text);
    return File.ReadAllText(Path.Combine(output, generated));
  }

  private static IEnumerable<TestCaseData> LevelsAndFixtures()
    => from profile in ProfileCase.All
       from fixture in EnumFixtures.CSharp
       select new TestCaseData(profile, fixture.Class, fixture.File).SetName($"{fixture.Class} in {profile}");

  /// <summary>Test design 1, 3 and 4: every call of every C# fixture computes in node, at every level, module format and typed-array setting, what .NET computes.</summary>
  [TestCaseSource(nameof(LevelsAndFixtures))]
  public void CSharpFixture_RunsInNodeLikeDotNet(ProfileCase profile, string className, string file) {
    var calls = EnumFixtures.CSharpCalls().Where(entry => entry.Class == className).Select(entry => entry.Call).ToList();
    var text = this.Translate(EnumFixtures.PathOf(file + ".cs"), "csharp", "javascript", className + profile.Parsed.Extension, profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    var outcomes = Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), className + profile.Parsed.Extension, calls, false, profile.Mode, profile.Global);
    var mismatches = calls.Select((call, index) => (call, Expected: EnumFixtures.Normalized(PrototypeFixture.InvokedOutcome(EnumFixtures.Compiled(className, call.Method), call)), Actual: outcomes[index]))
      .Where(result => result.Expected != result.Actual).Select(result => $"{result.call}: {result.Actual}, .NET {result.Expected}").ToList();
    Assert.That(mismatches.Take(10), Is.Empty, $"{mismatches.Count} of {calls.Count} in {profile}\n{text}");
  }

  /// <summary>Test design 1 and 12: the JavaScript fixture in every C# profile computes what node computes of the original.</summary>
  [Test]
  public void JavaScriptFixture_RunsInEveryCSharpProfileLikeNode() {
    var file = EnumFixtures.JavaScript;
    var calls = EnumFixtures.JavaScriptCalls().ToList();
    File.Copy(EnumFixtures.PathOf(file + ".js"), Path.Combine(this._directory, file + ".mjs"));
    var expected = Toolchain.NodeOutcomes(this._directory, file + ".mjs", calls);
    foreach (var profile in CSharpProfile.All) {
      var text = this.Translate(EnumFixtures.PathOf(file + ".js"), "javascript", "csharp", file + ".cs", "--profile", profile.Name);
      Assert.That(text, Does.Contain("public enum Color : int"));
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      var build = GeneratedCSharp.Compile(text, false);
      var mismatches = calls.Select((call, index) => (call, Expected: expected[index], Actual: build.Outcome(call)))
        .Where(result => result.Expected != result.Actual).Select(result => $"{result.call}: {result.Actual}, node {result.Expected}").ToList();
      Assert.That(mismatches, Is.Empty, $"{mismatches.Count} of {calls.Count} in {profile.Name}\n{text}");
    }
  }

  /// <summary>Test design 9: a public enum of 64 bits below ECMAScript 2020 is PLY1152, and nothing is written.</summary>
  [TestCase("es5", 3)]
  [TestCase("es2017", 3)]
  [TestCase("es2020", 0)]
  public void PublicLongEnum_IsRefusedBelowEs2020(string level, int exitCode) {
    var source = Path.Combine(this._directory, "Wide.cs");
    File.WriteAllText(source, "public enum Big : ulong { Max = ulong.MaxValue } public static class Wide { public static bool F(Big b) => b == Big.Max; }");
    var output = Path.Combine(this._directory, "out");
    var (code, text) = Toolchain.Polypiler(true, this._directory, ["translate", source, "--from", "csharp", "--to", "javascript", "--profile", level, "--module", "cjs", "--output", output]);
    Assert.That(code, Is.EqualTo(exitCode), text);
    if (exitCode != 0) {
      Assert.That(text, Does.Contain("PLY1152").And.Contain("the public 64-bit enum Big"));
      Assert.That(Directory.Exists(output) && Directory.EnumerateFiles(output).Any(), Is.False);
    }
  }

}
