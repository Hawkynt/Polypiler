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
/// The scenarios of <c>tests/features/prototype/value-records.feature</c> and the differential rows
/// of its test design, with the real <c>polypiler</c> executable, <c>node</c> and the C# compiler.
/// The expected outcome of a call is never computed here: it is what .NET computes of the original
/// C# fixture, built into this assembly, or what <c>node</c> computes of the original JavaScript.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
[Layer("core")]
public sealed class ValueRecordsTests {

  /// <summary>The JavaScript profiles the differential rows run: every level and module format, ECMAScript 5 with and without typed arrays.</summary>
  private static readonly IReadOnlyList<ProfileCase> _profiles = ProfileCase.All;

  private string _directory = "";

  /// <summary>Whether this case translates in process: the test design does, the scenarios start the executable.</summary>
  private bool _inProcess;

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Translates a file with the real executable, or in process where the test is about the generated file; returns the generated text, and the time it took is reported.</summary>
  private string Translate(string source, string from, string to, string generated, params string[] further) {
    var output = Path.Combine(this._directory, "out");
    var clock = Stopwatch.StartNew();
    var (exitCode, text) = Toolchain.Polypiler(this._inProcess, this._directory, ["translate", source, "--from", from, "--to", to, "--output", output, .. further]);
    TestContext.Out.WriteLine($"translate {Path.GetFileName(source)} --to {to} {string.Join(' ', further)}: {clock.ElapsedMilliseconds} ms");
    Assert.That(exitCode, Is.Zero, text);
    return File.ReadAllText(Path.Combine(output, generated));
  }

  private IReadOnlyList<string> Node(ProfileCase profile, string module, IReadOnlyList<PrototypeCall> calls)
    => Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), module + profile.Parsed.Extension, calls, true, profile.Mode, profile.Global);

  private static string DotNet(string className, PrototypeCall call) => PrototypeFixture.InvokedOutcome(RecordFixtures.Compiled(className, call.Method), call);

  /// <summary>Test design 1: every call of every C# fixture in every JavaScript level computes in node what .NET computes.</summary>
  [TestCaseSource(nameof(LevelsAndFixtures))]
  public void CSharpFixture_RunsInNodeLikeDotNet(ProfileCase profile, string file, string className) {
    this._inProcess = true;
    var calls = RecordFixtures.Calls().Where(call => call.File == file).ToList();
    var text = this.Translate(RecordFixtures.PathOf(file + ".cs"), "csharp", "javascript", className + profile.Parsed.Extension, profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    var outcomes = this.Node(profile, className, [.. calls.Select(call => call.Call)]);
    for (var index = 0; index < calls.Count; ++index)
      Assert.That(outcomes[index], Is.EqualTo(DotNet(className, calls[index].Call)), $"{calls[index].Call} in {profile}\n{text}");
  }

  private static IEnumerable<TestCaseData> LevelsAndFixtures()
    => from profile in _profiles
       from fixture in RecordFixtures.CSharp
       select new TestCaseData(profile, fixture.File, fixture.Class).SetName($"{fixture.File} in {profile}");

  /// <summary>Test design 1: the JavaScript fixture in every C# profile computes what node computes of the original.</summary>
  [Test]
  public void JavaScriptFixture_RunsInEveryCSharpProfileLikeNode() {
    this._inProcess = true;
    var random = new Random(2026);
    var calls = new List<PrototypeCall> { new("pathLength", [3.0]), new("pathLength", [1.0]), new("boxed", [1.0]), new("boxed", [0.0]) };
    for (var draw = 0; draw < 6; ++draw) {
      var numbers = Enumerable.Range(0, 4).Select(_ => (object)Math.Round(random.NextDouble() * 20 - 10, 2)).ToArray();
      calls.Add(new("midX", numbers));
      calls.Add(new("dotOf", numbers));
    }

    File.Copy(RecordFixtures.PathOf("geometry.js"), Path.Combine(this._directory, "geometry.mjs"));
    var expected = Toolchain.NodeOutcomes(this._directory, "geometry.mjs", calls, true);
    foreach (var profile in CSharpProfile.All) {
      var text = this.Translate(RecordFixtures.PathOf("geometry.js"), "javascript", "csharp", "geometry.cs", "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      Assert.That(calls.Select(GeneratedCSharp.Compile(text, false).Outcome), Is.EqualTo(expected), profile.Name);
    }
  }

}
