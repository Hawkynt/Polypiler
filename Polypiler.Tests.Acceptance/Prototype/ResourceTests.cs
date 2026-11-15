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
/// The differential rows of the test design of <c>tests/features/ir/ir-resource-scopes.feature</c>,
/// with the real <c>polypiler</c> executable in process, <c>node</c> and the C# compiler: the log of
/// every scope and the message of the exception that leaves it agree between .NET, node, the
/// interpreter and every profile of both backends. The expected outcome of a call is never computed
/// here: it is what .NET computes of <c>Scopes.cs</c>, built into this assembly, or what <c>node</c>
/// computes of <c>scopes.js</c>.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("core")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class ResourceTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Translates a file in process and returns the generated text; the time it took is reported.</summary>
  private string Translate(string source, string to, string generated, string output, params string[] further) {
    var directory = Path.Combine(this._directory, output);
    var clock = Stopwatch.StartNew();
    var (exitCode, text) = Toolchain.Polypiler(true, this._directory, ["translate", source, "--from", source.EndsWith(".cs", StringComparison.Ordinal) ? "csharp" : "javascript", "--to", to, "--output", directory, .. further]);
    TestContext.Out.WriteLine($"translate {Path.GetFileName(source)} --to {to} {string.Join(' ', further)}: {clock.ElapsedMilliseconds} ms");
    Assert.That(exitCode, Is.Zero, text);
    var written = Directory.GetFiles(directory).Single(path => Path.GetFileName(path) != "polypiler.manifest.json");
    Assert.That(Path.GetFileName(written), Is.EqualTo(generated));
    return File.ReadAllText(written);
  }

  private static IEnumerable<TestCaseData> Levels() => ProfileCase.All.Select(profile => new TestCaseData(profile).SetName($"Scopes in {profile}"));

  private static List<PrototypeCall> Calls() => [.. ResourceFixtures.Calls()];

  private static List<string> Expected(List<PrototypeCall> calls) => [.. calls.Select(call => PrototypeFixture.InvokedOutcome(ResourceFixtures.Compiled(call.Method), call))];

  /// <summary>Test design 1 to 6: every scope of the C# fixture computes in node at every level what .NET computes.</summary>
  [TestCaseSource(nameof(Levels))]
  public void CSharpFixture_RunsInNodeLikeDotNet(ProfileCase profile) {
    var calls = Calls();
    var text = this.Translate(ResourceFixtures.PathOf("Scopes.cs"), "javascript", ResourceFixtures.Class + profile.Parsed.Extension, "out", profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    var outcomes = Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), ResourceFixtures.Class + profile.Parsed.Extension, calls, true, profile.Mode, profile.Global);
    Assert.That(outcomes, Is.EqualTo(Expected(calls)), $"{profile}\n{text}");
  }

  /// <summary>Test design 1 to 7: every scope of the C# fixture computes in every C# profile what .NET computes.</summary>
  [Test]
  public void CSharpFixture_RunsInEveryCSharpProfileLikeDotNet() {
    var calls = Calls();
    var expected = Expected(calls);
    foreach (var profile in CSharpProfile.All) {
      var text = this.Translate(ResourceFixtures.PathOf("Scopes.cs"), "csharp", ResourceFixtures.Class + ".cs", profile.Name, "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      Assert.That(calls.Select(GeneratedCSharp.Compile(text, false).Outcome), Is.EqualTo(expected), $"{profile.Name}\n{text}");
    }
  }

  /// <summary>The calls of the JavaScript fixture and what node computes of the original.</summary>
  private (List<PrototypeCall> Calls, IReadOnlyList<string> Expected) Original() {
    List<PrototypeCall> calls = [.. ResourceFixtures.ScriptCalls()];
    var copy = Path.Combine(this._directory, "scopes.mjs");
    File.WriteAllText(copy, ResourceFixtures.SourceOf("scopes.js"));
    return (calls, Toolchain.NodeOutcomes(this._directory, Path.GetFileName(copy), calls, true));
  }

  /// <summary>Test design 1: the JavaScript fixture in every C# profile computes what node computes of the original, and stays a try ... finally.</summary>
  [Test]
  public void JavaScriptFixture_RunsInEveryCSharpProfileLikeNode() {
    var (calls, expected) = this.Original();
    foreach (var profile in CSharpProfile.All) {
      var text = this.Translate(ResourceFixtures.PathOf("scopes.js"), "csharp", "scopes.cs", profile.Name, "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      Assert.That(text, Does.Contain("finally").And.Not.Contain("using ("), text);
      Assert.That(calls.Select(GeneratedCSharp.Compile(text, false).Outcome), Is.EqualTo(expected), $"{profile.Name}\n{text}");
    }
  }

  /// <summary>Test design 1: the JavaScript fixture at every JavaScript level computes in node what the original computes.</summary>
  [Test]
  public void JavaScriptFixture_RunsInEveryJavaScriptProfileLikeNode() {
    var (calls, expected) = this.Original();
    foreach (var profile in ProfileCase.All) {
      var output = "js-" + Guid.NewGuid().ToString("N");
      var text = this.Translate(ResourceFixtures.PathOf("scopes.js"), "javascript", "scopes" + profile.Parsed.Extension, output, profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
      Assert.That(Toolchain.NodeOutcomes(Path.Combine(this._directory, output), "scopes" + profile.Parsed.Extension, calls, true, profile.Mode, profile.Global),
        Is.EqualTo(expected), $"{profile}\n{text}");
    }
  }

}
