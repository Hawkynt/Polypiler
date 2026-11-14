using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
public sealed class ValueRecordsTests {

  private static readonly string[] _scenarios = [
    "A struct stays a copy in every JavaScript profile",
    "Record-struct equality compiles and holds in every C# profile",
    "JavaScript object literals become C# structs",
    "A 128-bit addition on a struct of two halves agrees everywhere",
    "An object written through a second name is refused",
    "Comparing object literals by identity is refused"
  ];

  /// <summary>The JavaScript profiles the differential rows run: every level and module format, ECMAScript 5 with and without typed arrays.</summary>
  private static readonly IReadOnlyList<ProfileCase> _profiles = ProfileCase.All;

  /// <summary>The profile of a level in the module format the level starts with: an ES module, or CommonJS at ECMAScript 5.</summary>
  private static ProfileCase Level(string level) => _profiles.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

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

  /// <summary>Ensures the feature and its NUnit bindings stay in agreement.</summary>
  [Test]
  public void FeatureScenarios_HaveBindings() {
    var feature = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/features/prototype/value-records.feature"));
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario(?: Outline)?: (.+?)\r?$").Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
  }

  /// <summary>Scenario outline: A struct stays a copy in every JavaScript profile.</summary>
  [TestCase("es5")]
  [TestCase("es2015")]
  [TestCase("es2022")]
  public void Struct_StaysACopyInEveryJavaScriptProfile(string level) {
    var profile = Level(level);
    var text = this.Translate(RecordFixtures.PathOf("Geometry.cs"), "csharp", "javascript", "Geometry" + profile.Parsed.Extension, profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    PrototypeCall[] calls = [new("Copy", []), new("ArrayCopy", [])];
    Assert.That(this.Node(profile, "Geometry", calls), Is.EqualTo(new[] { "161", "397" }));
    Assert.That(calls.Select(call => DotNet("Geometry", call)), Is.EqualTo(new[] { "161", "397" }));
  }

  /// <summary>Scenario outline: Record-struct equality compiles and holds in every C# profile.</summary>
  [TestCase("net2.0", "public struct Money : global::System.IEquatable<Money>")]
  [TestCase("netstandard2.0", "public struct Money : global::System.IEquatable<Money>")]
  [TestCase("net10.0", "public record struct Money(long Cents, int Currency)")]
  public void RecordStructEquality_CompilesAndHoldsInEveryCSharpProfile(string profileName, string form) {
    var profile = CSharpProfile.Parse(profileName);
    var text = this.Translate(RecordFixtures.PathOf("Money.cs"), "csharp", "csharp", "Ledger.cs", "--profile", profileName);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var build = GeneratedCSharp.Compile(text, true);
    Assert.That(build.Method("Same").Invoke(null, [150L, 150L]), Is.True);
    Assert.That(build.Method("Same").Invoke(null, [150L, 151L]), Is.False);
    Assert.That(build.Method("NaNEqual").Invoke(null, []), Is.True);
    Assert.That(text, Does.Contain("\n" + form));
  }

  /// <summary>Scenario: JavaScript object literals become C# structs.</summary>
  [Test]
  public void JavaScriptObjectLiterals_BecomeCSharpStructs() {
    var text = this.Translate(RecordFixtures.PathOf("geometry.js"), "javascript", "csharp", "geometry.cs", "--profile", "net10.0");
    Assert.That(text, Does.Contain("public readonly struct Point"));
    PrototypeCall[] calls = [new("midX", [1.0, 2.0, 4.0, 6.0]), new("dotOf", [1.0, 2.0, 3.0, 4.0])];
    File.Copy(RecordFixtures.PathOf("geometry.js"), Path.Combine(this._directory, "geometry.mjs"));
    Assert.That(Toolchain.NodeOutcomes(this._directory, "geometry.mjs", calls, true), Is.EqualTo(new[] { "f:4004000000000000", "11" }));
    var build = GeneratedCSharp.Compile(text, true);
    Assert.That(calls.Select(build.Outcome), Is.EqualTo(new[] { "f:4004000000000000", "11" }));
  }

  /// <summary>Scenario: A 128-bit addition on a struct of two halves agrees everywhere.</summary>
  [Test]
  public void Addition128_AgreesEverywhere() {
    PrototypeCall[] calls = [
      new("AddLo", [ulong.MaxValue, 0UL, 1UL, 0UL]) { Returns = typeof(ulong) },
      new("AddHi", [ulong.MaxValue, 0UL, 1UL, 0UL]) { Returns = typeof(ulong) }
    ];
    string[] expected = ["0", "1"];
    Assert.That(calls.Select(call => DotNet("Wide128", call)), Is.EqualTo(expected));
    Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(RecordFixtures.Imported("U128"), call)), Is.EqualTo(expected));
    foreach (var profile in CSharpProfile.All) {
      var text = this.Translate(RecordFixtures.PathOf("U128.cs"), "csharp", "csharp", "Wide128.cs", "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      Assert.That(calls.Select(GeneratedCSharp.Compile(text, true).Outcome), Is.EqualTo(expected), profile.Name);
    }

    foreach (var level in new[] { "es5", "es2020", "es2022" }) {
      var profile = Level(level);
      this.Translate(RecordFixtures.PathOf("U128.cs"), "csharp", "javascript", "Wide128" + profile.Parsed.Extension, profile.Arguments);
      Assert.That(this.Node(profile, "Wide128", calls), Is.EqualTo(expected), level);
    }
  }

  /// <summary>Scenario: An object written through a second name is refused.</summary>
  [Test]
  public void ObjectWrittenThroughASecondName_IsRefused() {
    File.Copy(RecordFixtures.PathOf("aliasing.js"), Path.Combine(this._directory, "aliasing.js"));
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", "aliasing.js", "--from", "javascript", "--to", "csharp", "--output", "out");
    Assert.That(exitCode, Is.EqualTo(3));
    Assert.That(output, Does.Contain("PLY1102").And.Contain("an object literal written after it is built"));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
  }

  /// <summary>Scenario: Comparing object literals by identity is refused.</summary>
  [Test]
  public void ComparingObjectLiteralsByIdentity_IsRefused() {
    File.WriteAllText(Path.Combine(this._directory, "f.js"), "export function f(a) { const p = { x: a }; const q = { x: a }; return p === q; }");
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", "f.js", "--from", "javascript", "--to", "csharp", "--output", "out");
    Assert.That(exitCode, Is.EqualTo(3));
    Assert.That(output, Does.Contain("PLY1102").And.Contain("==="));
  }

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
