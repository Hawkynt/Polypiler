using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// Test design 1 and 2 of #1315 with the real <c>polypiler</c> executable, <c>node</c> and the C#
/// compiler: every fixture of <c>tests/fixtures/prototype/numtext</c> translated into every profile of
/// the other language, every call against .NET running the original C# fixture, built into this
/// assembly, or <c>node</c> running the original JavaScript - among them a thousand doubles and five
/// hundred floats of the reference vectors, which the helpers of JavaScript have to write as .NET does.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("core")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class NumberTextTests {

  /// <summary>How many doubles and floats of the reference vectors the fixtures are called with here.</summary>
  private const int Vectors = 1000;

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Translates a file; returns the generated text, and the time it took is reported.</summary>
  private string Translate(string source, string from, string to, string generated, params string[] further) {
    var output = Path.Combine(this._directory, "out");
    var clock = Stopwatch.StartNew();
    var (exitCode, text) = Toolchain.Polypiler(true, this._directory, ["translate", source, "--from", from, "--to", to, "--output", output, .. further]);
    TestContext.Out.WriteLine($"translate {Path.GetFileName(source)} --to {to} {string.Join(' ', further)}: {clock.ElapsedMilliseconds} ms");
    Assert.That(exitCode, Is.Zero, text);
    return File.ReadAllText(Path.Combine(output, generated));
  }

  private static IEnumerable<TestCaseData> LevelsAndFixtures()
    => from profile in ProfileCase.All
       from fixture in NumberTextFixtures.CSharp
       select new TestCaseData(profile, fixture.Class, fixture.File).SetName($"{fixture.Class} in {profile}");

  /// <summary>Test design 1 and 2: every call of every C# fixture computes in node, at every level, module format and typed-array setting, what .NET computes.</summary>
  [TestCaseSource(nameof(LevelsAndFixtures))]
  public void CSharpFixture_RunsInNodeLikeDotNet(ProfileCase profile, string className, string file) {
    var calls = NumberTextFixtures.CSharpCalls(Vectors).Where(entry => entry.Class == className).Select(entry => entry.Call).ToList();
    var text = this.Translate(NumberTextFixtures.PathOf(file + ".cs"), "csharp", "javascript", className + profile.Parsed.Extension, profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    var outcomes = Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), className + profile.Parsed.Extension, calls, true, profile.Mode, profile.Global);
    var mismatches = calls.Select((call, index) => (call, Expected: PrototypeFixture.InvokedOutcome(NumberTextFixtures.Compiled(className, call.Method), call), Actual: outcomes[index]))
      .Where(result => result.Expected != result.Actual).Select(result => $"{result.call}: {result.Actual}, .NET {result.Expected}").ToList();
    Assert.That(mismatches.Take(10), Is.Empty, $"{mismatches.Count} of {calls.Count} in {profile}\n{text}");
  }

  /// <summary>Test design 1: every JavaScript fixture in every C# profile computes what node computes of the original.</summary>
  [TestCaseSource(typeof(NumberTextFixtures), nameof(NumberTextFixtures.JavaScript))]
  public void JavaScriptFixture_RunsInEveryCSharpProfileLikeNode(string file) {
    var calls = NumberTextFixtures.JavaScriptCalls(Vectors).Where(entry => entry.File == file).Select(entry => entry.Call).ToList();
    File.Copy(NumberTextFixtures.PathOf(file + ".js"), Path.Combine(this._directory, file + ".mjs"));
    var expected = Toolchain.NodeOutcomes(this._directory, file + ".mjs", calls, true);
    foreach (var profile in CSharpProfile.All) {
      var text = this.Translate(NumberTextFixtures.PathOf(file + ".js"), "javascript", "csharp", file + ".cs", "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      var build = GeneratedCSharp.Compile(text, false);
      var mismatches = calls.Select((call, index) => (call, Expected: expected[index], Actual: build.Outcome(call)))
        .Where(result => result.Expected != result.Actual).Select(result => $"{result.call}: {result.Actual}, node {result.Expected}").ToList();
      Assert.That(mismatches.Take(10), Is.Empty, $"{mismatches.Count} of {calls.Count} in {profile.Name}\n{text}");
    }
  }

}
