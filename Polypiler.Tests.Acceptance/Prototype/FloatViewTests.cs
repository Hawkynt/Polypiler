using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The scenarios of <c>tests/features/prototype/float-view.feature</c>, with the real <c>polypiler</c>
/// executable, the C# compiler and <c>node</c>, and the differential measurement of the issue: the
/// endian loads and stores of <c>f32</c> and <c>f64</c> in every JavaScript profile, and both fixtures
/// translated to every profile of both backends, against .NET. A stored NaN is compared by class (ADR 0005).
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class FloatViewTests {

  private static readonly string[] _scenarios = [
    "A JavaScript DataView float read translates to C# for every profile",
    "A C# float store translates to JavaScript for every level and writes the same bytes",
    "Negative zero keeps its sign bit in both directions",
    "A read outside the buffer traps",
    "A computed byte order is refused",
    "Half precision stays refused"
  ];

  private static readonly bool[] _overflowChecking = [false, true];

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Ensures the feature and its NUnit bindings stay in agreement.</summary>
  [Test]
  public void FeatureScenarios_HaveBindings() {
    var feature = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/features/prototype/float-view.feature"));
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario(?: Outline)?: (.+?)\r?$").Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
  }

  private string Copy(string fixture) {
    var name = Path.GetFileName(fixture);
    File.Copy(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", fixture), Path.Combine(this._directory, name), true);
    return name;
  }

  /// <summary>Translates a file of the working directory with the real executable; the one file it writes, with its text.</summary>
  private (string Path, string Text) Translate(string source, string from, string to, params string[] further) {
    var output = Path.Combine(this._directory, "out-" + Guid.NewGuid().ToString("N"));
    var (exitCode, text) = Toolchain.RunPolypiler(this._directory, ["translate", source, "--from", from, "--to", to, "--output", output, .. further]);
    Assert.That(exitCode, Is.Zero, text);
    var file = Directory.GetFiles(output).Single();
    return (file, File.ReadAllText(file));
  }

  /// <summary>A C# translation for a profile: compiled against the profile's reference assemblies, then built for this runtime.</summary>
  private GeneratedCSharp CSharp(string source, string from, CSharpProfile profile, bool checking = false) {
    var (_, text) = this.Translate(source, from, "csharp", "--profile", profile.Name);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
    return GeneratedCSharp.Compile(text, checking);
  }

  /// <summary>What node does with a JavaScript translation for a profile.</summary>
  private IReadOnlyList<string> Node(string source, string from, ProfileCase profile, IReadOnlyList<PrototypeCall> calls) {
    var (file, text) = this.Translate(source, from, "javascript", profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, profile.ToString());
    return Toolchain.NodeOutcomes(Path.GetDirectoryName(file)!, Path.GetFileName(file), calls, true, profile.Mode, profile.Global);
  }

  /// <summary>
  /// What node does with the JavaScript fixture as it is written. A <c>DataView</c> reports a range it
  /// does not hold in its own words; any <c>RangeError</c> is the IR's index out of range.
  /// </summary>
  private IReadOnlyList<string> Original(IReadOnlyList<PrototypeCall> calls) {
    File.Copy(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "js", "FloatView.js"), Path.Combine(this._directory, "FloatView.mjs"), true);
    return [.. Toolchain.NodeOutcomes(this._directory, "FloatView.mjs", calls, true)
      .Select(outcome => outcome.StartsWith("trap: ", StringComparison.Ordinal) ? "trap: index out of range" : outcome)];
  }

  private static string Bytes(string text) => "[" + string.Join(",", text.Split(' ').Select(pair => byte.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture))) + "]";

  /// <summary>Scenario outline: a JavaScript DataView float read translates to C# for every profile.</summary>
  [TestCase("net2.0")]
  [TestCase("netstandard2.1")]
  [TestCase("net10.0")]
  public void JavaScriptFloatRead_TranslatesToCSharp(string profile) {
    PrototypeCall[] calls = [
      new("getF32LE", [new byte[] { 0x00, 0x00, 0x80, 0x3F }, 0]) { Returns = typeof(float) },
      new("getF64BE", [new byte[] { 0x40, 0x09, 0x21, 0xFB, 0x54, 0x44, 0x2D, 0x18 }, 0]) { Returns = typeof(double) }
    ];
    string[] expected = ["1 | " + Bytes("00 00 80 3F"), "f:400921fb54442d18 | " + Bytes("40 09 21 FB 54 44 2D 18")];
    Assert.That(BitConverter.Int64BitsToDouble(0x400921fb54442d18), Is.EqualTo(3.141592653589793));
    Assert.That(this.Original(calls), Is.EqualTo(expected), "node");
    var build = this.CSharp(this.Copy("js/FloatView.js"), "javascript", CSharpProfile.Parse(profile));
    Assert.That(calls.Select(build.Outcome), Is.EqualTo(expected), profile);
  }

  /// <summary>Scenario outline: a C# float store translates to JavaScript for every level and writes the same bytes.</summary>
  [TestCase("es5", false, 0.1, "CD CC CC 3D")]
  [TestCase("es5", true, 1e40, "00 00 80 7F")]
  [TestCase("es2015", false, 3.4028235677973366e38, "00 00 80 7F")]
  [TestCase("es2022", false, 1.0509738482436128e-45, "01 00 00 00")]
  public void CSharpFloatStore_WritesTheSameBytesInNode(string level, bool typed, double x, string bytes) {
    var call = new PrototypeCall("PutF32LE", [new byte[4], 0, x]);
    var expected = $"{Bytes(bytes)} | {Bytes(bytes)}";
    Assert.That(PrototypeFixture.CompiledOutcome("float/FloatView", call), Is.EqualTo(expected), ".NET");
    var profile = new ProfileCase(level, level == "es5" ? "cjs" : "esm", typed, null);
    Assert.That(this.Node(this.Copy("float/FloatView.cs"), "csharp", profile, [call]).Single(), Is.EqualTo(expected), profile.ToString());
  }

  /// <summary>Scenario: negative zero keeps its sign bit in both directions, in every profile of both backends.</summary>
  [Test]
  public void NegativeZero_KeepsItsSignBit() {
    var expected = $"{Bytes("80 00 00 00 00 00 00 00")} | {Bytes("80 00 00 00 00 00 00 00")}";
    var call = new PrototypeCall("PutF64BE", [new byte[8], 0, -0.0]);
    var script = call with { Method = "putF64BE" };
    Assert.That(PrototypeFixture.CompiledOutcome("float/FloatView", call), Is.EqualTo(expected), ".NET");
    Assert.That(this.Original([script]).Single(), Is.EqualTo(expected), "node");
    var outcomes = new Dictionary<string, string>();
    foreach (var profile in CSharpProfile.All) {
      outcomes[$"C# to {profile.Name}"] = this.CSharp(this.Copy("float/FloatView.cs"), "csharp", profile, true).Outcome(call);
      outcomes[$"JavaScript to {profile.Name}"] = this.CSharp(this.Copy("js/FloatView.js"), "javascript", profile, true).Outcome(script);
    }

    foreach (var profile in ProfileCase.All) {
      outcomes[$"C# to {profile}"] = this.Node(this.Copy("float/FloatView.cs"), "csharp", profile, [call]).Single();
      outcomes[$"JavaScript to {profile}"] = this.Node(this.Copy("js/FloatView.js"), "javascript", profile, [script]).Single();
    }

    Assert.That(outcomes.Where(run => run.Value != expected).Select(run => $"{run.Key}: {run.Value}"), Is.Empty);
  }

  /// <summary>Scenario: a read outside the 12-byte buffer traps in node and in the C# translation of every profile.</summary>
  [Test]
  public void ReadOutsideTheBuffer_Traps() {
    var call = new PrototypeCall("getF32LE", [new byte[FloatViewCases.Length], 9]) { Returns = typeof(float) };
    var trap = "trap: index out of range";
    var (exitCode, output) = Toolchain.RunNode(this._directory, this.Script(
      "const view = new DataView(new Uint8Array(12).buffer); try { view.getFloat32(9, true); console.log('read'); } catch (error) { console.log(error.constructor.name); }"));
    Assert.That((exitCode, output.Trim()), Is.EqualTo((0, "RangeError")));
    Assert.That(this.Original([call]).Single(), Is.EqualTo(trap));
    foreach (var profile in CSharpProfile.All)
      foreach (var checking in _overflowChecking)
        Assert.That(this.CSharp(this.Copy("js/FloatView.js"), "javascript", profile, checking).Outcome(call), Is.EqualTo(trap), profile.Name);
  }

  private string Script(string text) {
    var path = Path.Combine(this._directory, "script-" + Guid.NewGuid().ToString("N") + ".mjs");
    File.WriteAllText(path, text);
    return path;
  }

  /// <summary>Scenarios: a computed byte order and half precision are refused, and nothing is written.</summary>
  [TestCase("/** @param {Uint8Array} b @param {boolean} le */ export function f(b, le) { const v = new DataView(b.buffer); return v.getFloat32(0, le); }",
    "DataView.getFloat32 with a byte order that is not a literal")]
  [TestCase("/** @param {Uint8Array} b */ export function f(b) { const v = new DataView(b.buffer); return v.getFloat16(0, true); }",
    "the DataView method getFloat16")]
  public void Refusal_IsReported(string function, string message) {
    File.WriteAllText(Path.Combine(this._directory, "snippet.mjs"), function);
    var output = Path.Combine(this._directory, "out");
    var (exitCode, text) = Toolchain.RunPolypiler(this._directory, "translate", "snippet.mjs", "--from", "javascript", "--to", "csharp", "--output", output);
    Assert.That(exitCode, Is.EqualTo(3), text);
    Assert.That(text, Does.Contain("PLY1102").And.Contain(message));
    Assert.That(Directory.Exists(output), Is.False);
  }

  /// <summary>
  /// The differential measurement for JavaScript: the eight operations in every profile run by node,
  /// on every boundary, every class of value and 1000 random bit patterns per operation (seed 2026),
  /// have the outcome of .NET's <c>BinaryPrimitives</c>; a stored NaN as a NaN.
  /// </summary>
  [TestCaseSource(typeof(ProfileCase), nameof(ProfileCase.All))]
  public void EveryOperation_ComputesBinaryPrimitives(ProfileCase profile) {
    var random = new Random(IntegerCases.Seed);
    var cases = FloatViewCases.All.SelectMany(@case => FloatViewCases.Calls(@case, random).Select(call => (Case: @case, Call: call))).ToList();
    var calls = cases.Select(entry => entry.Call).ToList();
    var file = JavaScriptEmitter.Emit(FloatViewCases.Module(), profile.Parsed).Single();
    Assert.That(GeneratedJavaScript.Problems(file.Content, profile.Parsed), Is.Empty);
    File.WriteAllText(Path.Combine(this._directory, file.Path), file.Content);
    var node = Toolchain.NodeOutcomes(this._directory, file.Path, calls, true, profile.Mode, profile.Global);
    var bitwise = 0;
    var mismatches = new List<string>();
    for (var index = 0; index < cases.Count; ++index) {
      var expected = FloatViewCases.Expected(cases[index].Case, calls[index]);
      if (node[index] == expected)
        ++bitwise;
      else if (!FloatViewCases.Matches(cases[index].Case, calls[index], expected, node[index]))
        mismatches.Add($"{calls[index]}: node {node[index]}, BinaryPrimitives {expected}");
    }

    TestContext.Out.WriteLine($"{FloatViewCases.All.Count} operations as {profile}: {calls.Count} calls, {bitwise} identical, {calls.Count - bitwise - mismatches.Count} NaN by class, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

  /// <summary>
  /// Test design 1 and the differential measurement for the fixtures: the C# fixture in every
  /// JavaScript profile, and the JavaScript fixture as node runs it and in every profile of both
  /// backends, have the outcomes of the C# fixture on .NET - the WAV reader among them.
  /// </summary>
  [Test]
  public void Fixtures_ComputeWhatDotNetComputes_InEveryProfile() {
    var random = new Random(IntegerCases.Seed);
    var calls = FloatViewCases.FixtureMethods.SelectMany(method => FloatViewCases.FixtureCalls(method, random)).ToList();
    var expected = calls.Select(call => PrototypeFixture.CompiledOutcome("float/FloatView", call)).ToList();
    var scripted = calls.Where(call => !call.Method.StartsWith("To", StringComparison.Ordinal)).ToList();
    var scriptCalls = scripted.Select(call => call with { Method = FloatViewCases.Script(call.Method) }).ToList();
    var scriptExpected = scripted.Select(call => expected[calls.IndexOf(call)]).ToList();
    var mismatches = new List<string>();
    var runs = 0;
    void Compare(string where, IReadOnlyList<PrototypeCall> asked, IReadOnlyList<PrototypeCall> named, IReadOnlyList<string> wanted, IReadOnlyList<string> outcomes) {
      ++runs;
      for (var index = 0; index < asked.Count; ++index)
        if (!FloatViewCases.FixtureMatches(asked[index].Method, asked[index], wanted[index], outcomes[index]))
          mismatches.Add($"{where}: {named[index]} is {outcomes[index]}, .NET {wanted[index]}");
    }

    Compare("node", scripted, scriptCalls, scriptExpected, this.Original(scriptCalls));
    foreach (var profile in ProfileCase.All) {
      Compare($"C# to {profile}", calls, calls, expected, this.Node(this.Copy("float/FloatView.cs"), "csharp", profile, calls));
      Compare($"JavaScript to {profile}", scripted, scriptCalls, scriptExpected, this.Node(this.Copy("js/FloatView.js"), "javascript", profile, scriptCalls));
    }

    foreach (var profile in CSharpProfile.All) {
      var build = this.CSharp(this.Copy("js/FloatView.js"), "javascript", profile);
      Compare($"JavaScript to {profile.Name}", scripted, scriptCalls, scriptExpected, [.. scriptCalls.Select(build.Outcome)]);
    }

    TestContext.Out.WriteLine($"{calls.Count} calls, {runs} runs, {mismatches.Count} mismatches; node {Toolchain.NodeVersion()}, .NET {Environment.Version}");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} outcomes differ");
  }

}
