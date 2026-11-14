using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
[Layer("core")]
[Category("Prototype")]
public sealed class FloatViewTests {

  private static readonly bool[] _overflowChecking = [false, true];

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

  private string Copy(string fixture) {
    var name = Path.GetFileName(fixture);
    File.Copy(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", fixture), Path.Combine(this._directory, name), true);
    return name;
  }

  /// <summary>Translates a file of the working directory with the real executable, or in process where the test is about the generated file; the one file it writes, with its text.</summary>
  private (string Path, string Text) Translate(string source, string from, string to, params string[] further) {
    var output = Path.Combine(this._directory, "out-" + Guid.NewGuid().ToString("N"));
    var (exitCode, text) = Toolchain.Polypiler(this._inProcess, this._directory, ["translate", source, "--from", from, "--to", to, "--output", output, .. further]);
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
  private IReadOnlyList<string> Original(IReadOnlyList<PrototypeCall> calls) => Original(this._directory, calls);

  /// <summary>What node does with the JavaScript fixture as it is written, copied into a directory as a module.</summary>
  internal static IReadOnlyList<string> Original(string directory, IReadOnlyList<PrototypeCall> calls) {
    File.Copy(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "js", "FloatView.js"), Path.Combine(directory, "FloatView.mjs"), true);
    return [.. Toolchain.NodeOutcomes(directory, "FloatView.mjs", calls, true)
      .Select(outcome => outcome.StartsWith("trap: ", StringComparison.Ordinal) ? "trap: index out of range" : outcome)];
  }

  /// <summary>Bytes written in hexadecimal pairs, as an outcome shows an array of them.</summary>
  internal static string Bytes(string text) => "[" + string.Join(",", text.Split(' ').Select(pair => byte.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture))) + "]";

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
  [TestCaseSource(typeof(TargetCase), nameof(TargetCase.All))]
  public void Fixtures_ComputeWhatDotNetComputes_InEveryProfile(TargetCase target) {
    this._inProcess = true;
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
    if (target.JavaScript is { } profile) {
      Compare($"C# to {profile}", calls, calls, expected, this.Node(this.Copy("float/FloatView.cs"), "csharp", profile, calls));
      Compare($"JavaScript to {profile}", scripted, scriptCalls, scriptExpected, this.Node(this.Copy("js/FloatView.js"), "javascript", profile, scriptCalls));
    } else {
      var build = this.CSharp(this.Copy("js/FloatView.js"), "javascript", target.CSharp!);
      Compare($"JavaScript to {target}", scripted, scriptCalls, scriptExpected, [.. scriptCalls.Select(build.Outcome)]);
    }

    TestContext.Out.WriteLine($"{calls.Count} calls, {runs} runs, {mismatches.Count} mismatches; node {Toolchain.NodeVersion()}, .NET {Environment.Version}");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} outcomes differ");
  }

}
