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
/// The differential rows of the test design of <c>tests/features/prototype/optional-values.feature</c>
/// with the real <c>polypiler</c> executable in process, <c>node</c> and the C# compiler. The expected
/// outcome of a call is never computed here: it is what .NET computes of <c>Find.cs</c>, built into
/// this assembly, or what <c>node</c> computes of <c>find.js</c>.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class OptionalValuesTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Translates a fixture in process and returns the generated file's path and text; the time it took is reported.</summary>
  private (string Path, string Text) Translate(string file, string to, string output, params string[] further) {
    var source = OptionalFixtures.PathOf(file);
    var directory = Path.Combine(this._directory, output);
    var clock = Stopwatch.StartNew();
    var (exitCode, text) = Toolchain.Polypiler(true, this._directory, ["translate", source, "--from", file.EndsWith(".cs", StringComparison.Ordinal) ? "csharp" : "javascript", "--to", to, "--output", directory, .. further]);
    TestContext.Out.WriteLine($"translate {file} --to {to} {string.Join(' ', further)}: {clock.ElapsedMilliseconds} ms");
    Assert.That(exitCode, Is.Zero, text);
    var generated = Directory.GetFiles(directory).Single();
    return (generated, File.ReadAllText(generated));
  }

  /// <summary>Test design 1, 3, 5, 6 and 8: every call of Find.cs in every JavaScript level and module format computes in node what .NET computes.</summary>
  [TestCaseSource(typeof(ProfileCase), nameof(ProfileCase.All))]
  public void CSharpFixture_RunsInNodeLikeDotNet(ProfileCase profile) {
    var calls = OptionalFixtures.Calls().ToList();
    var (path, text) = this.Translate("Find.cs", "javascript", "out", profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    var outcomes = Toolchain.NodeOutcomes(Path.GetDirectoryName(path)!, Path.GetFileName(path), calls, true, profile.Mode, profile.Global);
    for (var index = 0; index < calls.Count; ++index)
      Assert.That(outcomes[index], Is.EqualTo(PrototypeFixture.InvokedOutcome(OptionalFixtures.Compiled(calls[index].Method), calls[index])), $"{calls[index]} in {profile}\n{text}");
  }

  /// <summary>The calls of find.js, each with what node prints for it.</summary>
  private static List<PrototypeCall> JavaScriptCalls() => [
    new("findOr", [new[] { 4, 5, 6 }, 6.0]), new("findOr", [new[] { 4, 5, 6 }, 7.0]),
    new("next", [null!]), new("next", [2.0]), new("isNull", [null!]), new("isNull", [0.0]), new("or5", [0.0]), new("or5", [null!]),
    new("assigned", []), new("length", [null!]), new("length", [new int[3]]),
    new("coerced", [-3.0]), new("coerced", [2.0]), new("missing", [-3.0]), new("missing", [2.0]),
    new("positive", [0.0]), new("positive", [2.0]), new("either", [0.0]), new("nothing", [0.0]), new("scaled", [2.0]),
    new("truthy", [-1.0]), new("truthy", [3.0]), new("truthy", [0.5])
  ];

  /// <summary>What node prints of the calls on find.js itself.</summary>
  private IReadOnlyList<string> Node(IReadOnlyList<PrototypeCall> calls) {
    var copy = Path.Combine(this._directory, "original.mjs");
    File.Copy(OptionalFixtures.PathOf("find.js"), copy, true);
    return Toolchain.NodeOutcomes(this._directory, "original.mjs", calls);
  }

  /// <summary>Test design 1 and 4: find.js in every C# profile computes what node computes of it; undefined and null are both the one absent value of C#.</summary>
  [Test]
  public void JavaScriptFixture_RunsInEveryCSharpProfileLikeNode() {
    var calls = JavaScriptCalls();
    var expected = this.Node(calls);
    foreach (var profile in CSharpProfile.All) {
      var (_, text) = this.Translate("find.js", "csharp", profile.Name, "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      Assert.That(calls.Select(GeneratedCSharp.Compile(text, false).Outcome), Is.EqualTo(expected), $"{profile.Name}\n{text}");
    }
  }

  /// <summary>Test design 1 and 4: find.js written back to JavaScript in every level computes what node computes of it, undefined where the source returns undefined.</summary>
  [TestCaseSource(typeof(ProfileCase), nameof(ProfileCase.All))]
  public void JavaScriptFixture_RunsInEveryLevelLikeNode(ProfileCase profile) {
    var calls = JavaScriptCalls();
    var expected = this.Node(calls);
    var (path, text) = this.Translate("find.js", "javascript", "out", profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    var outcomes = Toolchain.NodeOutcomes(Path.GetDirectoryName(path)!, Path.GetFileName(path), calls, false, profile.Mode, profile.Global);
    Assert.That(outcomes, Is.EqualTo(expected), text);
    if (profile.Global is null)
      Assert.That(ClassesSteps.Node(path, profile.Module == "esm", "positive", "const r = positive(0); console.log(r === undefined ? 'undefined' : String(r));"), Is.EqualTo("undefined"), text);
  }

}
