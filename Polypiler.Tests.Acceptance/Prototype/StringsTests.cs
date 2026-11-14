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
/// The differential rows of the test design of <c>tests/features/ir/ir-strings.feature</c>, with the
/// real <c>polypiler</c> executable, <c>node</c> and the C# compiler. The expected outcome of a call
/// is never computed here: it is what .NET computes of the original C# fixture, built into this
/// assembly, or what <c>node</c> computes of the original JavaScript. A trap is compared as a trap:
/// .NET, the IR and JavaScript name the same failure differently until the traps are mapped.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
[Layer("core")]
public sealed class StringsTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Translates a file in process; returns the generated text, and the time it took is reported.</summary>
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
       from className in StringFixtures.CSharp
       select new TestCaseData(profile, className).SetName($"{className} in {profile}");

  /// <summary>Test design 1: every call of every C# fixture computes in node, at every level, module format and typed-array setting, what .NET computes.</summary>
  [TestCaseSource(nameof(LevelsAndFixtures))]
  public void CSharpFixture_RunsInNodeLikeDotNet(ProfileCase profile, string className) {
    var calls = StringFixtures.CSharpCalls().Where(entry => entry.Class == className).Select(entry => entry.Call).ToList();
    var text = this.Translate(StringFixtures.PathOf(className + ".cs"), "csharp", "javascript", className + profile.Parsed.Extension, profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    var outcomes = Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), className + profile.Parsed.Extension, calls, true, profile.Mode, profile.Global);
    for (var index = 0; index < calls.Count; ++index) {
      var expected = PrototypeFixture.InvokedOutcome(StringFixtures.Compiled(className, calls[index].Method), calls[index]);
      Assert.That(StringFixtures.Comparable(outcomes[index]), Is.EqualTo(StringFixtures.Comparable(expected)), $"{calls[index]} in {profile}\n{text}");
    }
  }

  /// <summary>Test design 1: every JavaScript fixture in every C# profile computes what node computes of the original.</summary>
  [TestCaseSource(typeof(StringFixtures), nameof(StringFixtures.JavaScript))]
  public void JavaScriptFixture_RunsInEveryCSharpProfileLikeNode(string file) {
    var calls = StringFixtures.JavaScriptCalls().Where(entry => entry.File == file).Select(entry => entry.Call).ToList();
    File.Copy(StringFixtures.PathOf(file + ".js"), Path.Combine(this._directory, file + ".mjs"));
    var expected = Toolchain.NodeOutcomes(this._directory, file + ".mjs", calls, true).Select(StringFixtures.Comparable).ToList();
    var className = file.Replace('-', '_');
    foreach (var profile in CSharpProfile.All) {
      var text = this.Translate(StringFixtures.PathOf(file + ".js"), "javascript", "csharp", className + ".cs", "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      var build = GeneratedCSharp.Compile(text, false);
      Assert.That(calls.Select(call => StringFixtures.Comparable(build.Outcome(call))), Is.EqualTo(expected), $"{profile.Name}\n{text}");
    }
  }

}
