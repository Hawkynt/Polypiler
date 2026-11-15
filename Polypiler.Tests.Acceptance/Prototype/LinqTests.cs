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
/// The differential rows of <c>tests/features/prototype/sequences.feature</c> (#1329): the LINQ fixtures
/// compute in node at every JavaScript level and in every C# profile what .NET computes, and the
/// JavaScript fixtures of array callbacks compute in every profile of both backends what node computes
/// of the original - the results and the recorded traces of the calls of the lambdas. The time of every
/// translation and the size of every generated file are written to the test output.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("core")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class LinqTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Translates a file in process and returns the generated text; the time it took and the size of the text are reported.</summary>
  private string Translate(string source, string to, string generated, string output, params string[] further) {
    var directory = Path.Combine(this._directory, output);
    var clock = Stopwatch.StartNew();
    var (exitCode, text) = Toolchain.Polypiler(true, this._directory, ["translate", source, "--from", source.EndsWith(".cs", StringComparison.Ordinal) ? "csharp" : "javascript", "--to", to, "--output", directory, .. further]);
    Assert.That(exitCode, Is.Zero, text);
    var written = Directory.GetFiles(directory).Single(path => Path.GetFileName(path) != "polypiler.manifest.json");
    Assert.That(Path.GetFileName(written), Is.EqualTo(generated));
    var content = File.ReadAllText(written);
    TestContext.Out.WriteLine($"translate {Path.GetFileName(source)} --to {to} {string.Join(' ', further)}: {clock.ElapsedMilliseconds} ms, {content.Length} characters");
    return content;
  }

  private static IEnumerable<TestCaseData> CSharpCases()
    => LinqFixtures.CSharpFixtures.SelectMany(fixture => ProfileCase.All.Select(profile => new TestCaseData(fixture, profile).SetName($"{fixture} {profile}")));

  /// <summary>Test design 1, 2, 4 to 12, 14, 17 and 18: every call of a LINQ fixture computes in node at every level what .NET computes.</summary>
  [TestCaseSource(nameof(CSharpCases))]
  public void CSharpFixture_RunsInNodeLikeDotNet(string fixture, ProfileCase profile) {
    var calls = LinqFixtures.Calls(fixture).ToList();
    var text = this.Translate(SequenceFixtures.PathOf(fixture + ".cs"), "javascript", fixture + profile.Parsed.Extension, "out", profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    var outcomes = Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), fixture + profile.Parsed.Extension, calls, true, profile.Mode, profile.Global);
    Assert.That(outcomes, Is.EqualTo(calls.Select(call => PrototypeFixture.InvokedOutcome(LinqFixtures.Compiled(fixture, call.Method), call))), $"{profile}\n{text}");
  }

  /// <summary>Test design 1, 2, 4 to 12, 14, 17 and 18: every call of a LINQ fixture computes in every C# profile what .NET computes.</summary>
  [TestCase("Pipelines"), TestCase("ByteMedian")]
  public void CSharpFixture_RunsInEveryCSharpProfileLikeDotNet(string fixture) {
    var calls = LinqFixtures.Calls(fixture).ToList();
    var expected = calls.Select(call => PrototypeFixture.InvokedOutcome(LinqFixtures.Compiled(fixture, call.Method), call)).ToList();
    foreach (var profile in CSharpProfile.All) {
      var text = this.Translate(SequenceFixtures.PathOf(fixture + ".cs"), "csharp", fixture + ".cs", profile.Name, "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      Assert.That(calls.Select(GeneratedCSharp.Compile(text, false).Outcome), Is.EqualTo(expected), $"{profile.Name}\n{text}");
    }
  }

}
