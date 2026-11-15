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
/// The differential rows of the test design of <c>tests/features/ir/ir-exceptions.feature</c>, with
/// the real <c>polypiler</c> executable in process, <c>node</c> and the C# compiler: the class that
/// selects each handler, the messages of the user exceptions and every returned value agree between
/// .NET, node, the interpreter and every profile of both backends. The expected outcome of a call is
/// never computed here: it is what .NET computes of the original C# fixture, built into this
/// assembly, or what <c>node</c> computes of the original JavaScript.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("core")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class ExceptionTests {

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

  private static IEnumerable<TestCaseData> LevelsAndFixtures()
    => from profile in ProfileCase.All
       from fixture in ExceptionFixtures.CSharp
       select new TestCaseData(profile, fixture.File, fixture.Class).SetName($"{fixture.File} in {profile}");

  /// <summary>Test design 1, 2, 3 and 6: every call of every C# fixture computes in node at every level what .NET computes.</summary>
  [TestCaseSource(nameof(LevelsAndFixtures))]
  public void CSharpFixture_RunsInNodeLikeDotNet(ProfileCase profile, string file, string className) {
    var calls = ExceptionFixtures.Calls().Where(call => call.File == file).ToList();
    var text = this.Translate(ExceptionFixtures.PathOf(file + ".cs"), "javascript", className + profile.Parsed.Extension, "out", profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    var outcomes = Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), className + profile.Parsed.Extension, [.. calls.Select(call => call.Call)], true, profile.Mode, profile.Global);
    for (var index = 0; index < calls.Count; ++index)
      Assert.That(outcomes[index], Is.EqualTo(PrototypeFixture.InvokedOutcome(ExceptionFixtures.Compiled(className, calls[index].Call.Method), calls[index].Call)), $"{calls[index].Call} in {profile}\n{text}");
  }

  /// <summary>Test design 1: every call of every C# fixture computes in every C# profile what .NET computes.</summary>
  [TestCase("Traps", "TrapsDemo"), TestCase("Classes", "ClassesDemo")]
  public void CSharpFixture_RunsInEveryCSharpProfileLikeDotNet(string file, string className) {
    var calls = ExceptionFixtures.Calls().Where(call => call.File == file).Select(call => call.Call).ToList();
    var expected = calls.Select(call => PrototypeFixture.InvokedOutcome(ExceptionFixtures.Compiled(className, call.Method), call)).ToList();
    foreach (var profile in CSharpProfile.All) {
      var text = this.Translate(ExceptionFixtures.PathOf(file + ".cs"), "csharp", className + ".cs", profile.Name, "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      Assert.That(calls.Select(GeneratedCSharp.Compile(text, false).Outcome), Is.EqualTo(expected), $"{profile.Name}\n{text}");
    }
  }

  /// <summary>The calls of the JavaScript fixture and what node computes of the original.</summary>
  private (List<PrototypeCall> Calls, IReadOnlyList<string> Expected) Original() {
    List<PrototypeCall> calls = [new("f", [0]), new("f", [1]), new("chain", []), new("later", [0.0]), new("later", [1.0]), new("fin", [])];
    var copy = Path.Combine(this._directory, "errors.mjs");
    File.WriteAllText(copy, ExceptionFixtures.SourceOf("errors.js"));
    return (calls, Toolchain.NodeOutcomes(this._directory, Path.GetFileName(copy), calls, true));
  }

  /// <summary>Test design 1 and 4: the JavaScript fixture in every C# profile computes what node computes of the original.</summary>
  [Test]
  public void JavaScriptFixture_RunsInEveryCSharpProfileLikeNode() {
    var (calls, expected) = this.Original();
    foreach (var profile in CSharpProfile.All) {
      var text = this.Translate(ExceptionFixtures.PathOf("errors.js"), "csharp", "errors.cs", profile.Name, "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      Assert.That(calls.Select(GeneratedCSharp.Compile(text, false).Outcome), Is.EqualTo(expected), $"{profile.Name}\n{text}");
    }
  }

  /// <summary>Test design 1 and 4: the JavaScript fixture at every JavaScript level computes in node what the original computes.</summary>
  [Test]
  public void JavaScriptFixture_RunsInEveryJavaScriptProfileLikeNode() {
    var (calls, expected) = this.Original();
    foreach (var profile in ProfileCase.All) {
      var output = "js-" + Guid.NewGuid().ToString("N");
      var text = this.Translate(ExceptionFixtures.PathOf("errors.js"), "javascript", "errors" + profile.Parsed.Extension, output, profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
      Assert.That(Toolchain.NodeOutcomes(Path.Combine(this._directory, output), "errors" + profile.Parsed.Extension, calls, true, profile.Mode, profile.Global),
        Is.EqualTo(expected), $"{profile}\n{text}");
    }
  }

}
