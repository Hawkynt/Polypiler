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
/// The differential rows of the test design of <c>tests/features/ir/ir-module-state.feature</c>:
/// every fixture of <c>tests/fixtures/prototype/state</c>, in both directions, in every profile and
/// module format, called in sequence in one fresh load of the generated file. The expected outcomes
/// are the values of the issue, which .NET reproduces of the C# fixture and node of the JavaScript one.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class ModuleStateTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Translates a fixture in process and returns the generated text; the time it took is reported.</summary>
  private (string Path, string Text) Translate(string file, string from, string to, params string[] further) {
    var output = Path.Combine(this._directory, "out-" + Guid.NewGuid().ToString("N"));
    var clock = Stopwatch.StartNew();
    var (exitCode, text) = Toolchain.Polypiler(true, this._directory, ["translate", StateFixtures.PathOf(file), "--from", from, "--to", to, "--output", output, .. further]);
    TestContext.Out.WriteLine($"translate {file} --to {to} {string.Join(' ', further)}: {clock.ElapsedMilliseconds} ms");
    Assert.That(exitCode, Is.Zero, text);
    var generated = Directory.GetFiles(output).Single(path => Path.GetFileName(path) != "polypiler.manifest.json");
    return (generated, File.ReadAllText(generated));
  }

  private static IReadOnlyList<string> Node((string Path, string Text) generated, ProfileCase profile, IEnumerable<PrototypeCall> calls) {
    Assert.That(GeneratedJavaScript.Problems(generated.Text, profile.Parsed), Is.Empty, generated.Text);
    return Toolchain.NodeOutcomes(Path.GetDirectoryName(generated.Path)!, Path.GetFileName(generated.Path), [.. calls], false, profile.Mode, profile.Global);
  }

  private static IEnumerable<TestCaseData> ProfilesAndFixtures()
    => from profile in ProfileCase.All
       from fixture in StateFixtures.All
       select new TestCaseData(profile, fixture.File).SetName($"{fixture.File} in {profile}");

  /// <summary>Test design 1, 6 and 11: every fixture, from C# and from JavaScript, in every level and module format computes in node what the issue states.</summary>
  [TestCaseSource(nameof(ProfilesAndFixtures))]
  public void Fixture_RunsInNodeAsExpected(ProfileCase profile, string file) {
    var fixture = StateFixtures.All.Single(entry => entry.File == file);
    var calls = StateFixtures.Sequence(file);
    var expected = StateFixtures.Expected(file);
    Assert.That(Node(this.Translate(file + ".cs", "csharp", "javascript", profile.Arguments), profile, calls), Is.EqualTo(expected), "from C#");
    Assert.That(Node(this.Translate(fixture.Script + ".js", "javascript", "javascript", profile.Arguments), profile, calls.Select(StateFixtures.Script)), Is.EqualTo(expected), "from JavaScript");
  }

  /// <summary>Test design 1: every fixture, from C# and from JavaScript, compiles in every C# profile without diagnostics and computes what the issue states.</summary>
  [TestCaseSource(typeof(StateFixtures), nameof(StateFixtures.All))]
  public void Fixture_RunsInEveryCSharpProfileAsExpected((string File, string Class, string Script) fixture) {
    var calls = StateFixtures.Sequence(fixture.File);
    var expected = StateFixtures.Expected(fixture.File);
    Assert.That(StateFixtures.Compiled(StateFixtures.SourceOf(fixture.File + ".cs"), calls), Is.EqualTo(expected), ".NET");
    foreach (var profile in CSharpProfile.All) {
      foreach (var (file, from, sequence) in new[] { (fixture.File + ".cs", "csharp", calls), (fixture.Script + ".js", "javascript", [.. calls.Select(StateFixtures.Script)]) }) {
        var (_, text) = this.Translate(file, from, "csharp", "--profile", profile.Name);
        Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile.Name}:\n{text}");
        Assert.That(StateFixtures.Compiled(text, sequence), Is.EqualTo(expected), $"{file} in {profile.Name}");
      }
    }
  }

  /// <summary>Test design 1: node computes of the original JavaScript fixtures what .NET computes of the C# ones.</summary>
  [TestCaseSource(typeof(StateFixtures), nameof(StateFixtures.All))]
  public void OriginalJavaScript_RunsInNodeAsExpected((string File, string Class, string Script) fixture) {
    File.Copy(StateFixtures.PathOf(fixture.Script + ".js"), Path.Combine(this._directory, fixture.Script + ".mjs"));
    var outcomes = Toolchain.NodeOutcomes(this._directory, fixture.Script + ".mjs", [.. StateFixtures.Sequence(fixture.File).Select(StateFixtures.Script)]);
    Assert.That(outcomes, Is.EqualTo(StateFixtures.Expected(fixture.File)));
  }

  /// <summary>Test design 10: the recognition rules move no access of a global; with and without them the generated code computes the same.</summary>
  [TestCase("Xorshift32")]
  [TestCase("Lazy")]
  public void TheRules_ChangeNothingAboutTheState(string file) {
    var profile = ProfileCase.Default;
    var calls = StateFixtures.Sequence(file);
    var optimized = Node(this.Translate(file + ".cs", "csharp", "javascript", profile.Arguments), profile, calls);
    var plain = Node(this.Translate(file + ".cs", "csharp", "javascript", [.. profile.Arguments, "--opt", "none"]), profile, calls);
    Assert.That((optimized, plain), Is.EqualTo((StateFixtures.Expected(file), StateFixtures.Expected(file))));
  }

}
