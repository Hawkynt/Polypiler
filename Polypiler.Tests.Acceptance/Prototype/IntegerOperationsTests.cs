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
/// The scenarios of <c>tests/features/prototype/integer-operations.feature</c>, with the real
/// <c>polypiler</c> executable: every output of a scenario is translated by it - that of the fixtures
/// through the command's own entry point, in process - every C# output is built by the C# compiler for
/// its profile's framework and for this runtime, every JavaScript output is parsed at its level and run
/// by node, and the interpreter runs the imported IR.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class IntegerOperationsTests {

  private static readonly bool[] _overflowChecking = [false, true];

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Translates a file of the working directory with the real executable; the one file it writes, with its name.</summary>
  private (string Path, string Text, TimeSpan Took) Translate(string source, string from, string to, params string[] further)
    => this.Translate(false, source, from, to, further);

  /// <summary>Translates a file of the working directory in process or with the real executable; the one file it writes, with its name.</summary>
  private (string Path, string Text, TimeSpan Took) Translate(bool inProcess, string source, string from, string to, params string[] further) {
    var output = Path.Combine(this._directory, "out-" + Guid.NewGuid().ToString("N"));
    var watch = Stopwatch.StartNew();
    var (exitCode, text) = Toolchain.Polypiler(inProcess, this._directory, ["translate", source, "--from", from, "--to", to, "--output", output, .. further]);
    Assert.That(exitCode, Is.Zero, text);
    var file = Directory.GetFiles(output).Single();
    return (file, File.ReadAllText(file), watch.Elapsed);
  }

  /// <summary>What node does with a generated JavaScript file for the calls.</summary>
  private IReadOnlyList<string> Node(string file, ProfileCase profile, IReadOnlyList<PrototypeCall> calls)
    => Toolchain.NodeOutcomes(Path.GetDirectoryName(file)!, Path.GetFileName(file), calls, false, profile.Mode, profile.Global);

  private string Copy(string fixture) {
    var name = Path.GetFileName(fixture);
    File.Copy(Path.Combine(BitsFixtures.Directory, name), Path.Combine(this._directory, name));
    return name;
  }

  /// <summary>
  /// Every C# fixture of the integer operations in every JavaScript profile run by node, and every
  /// JavaScript fixture as node runs the original, as the interpreter runs the import and as every
  /// JavaScript profile runs it again: the outcomes of the original.
  /// </summary>
  [TestCaseSource(nameof(CSharpFixturesAndLevels))]
  public void CSharpFixture_RunsInNodeAtEveryLevel(string fixture, ProfileCase profile) {
    var source = this.Copy(fixture + ".cs");
    var calls = BitsFixtures.Calls(fixture);
    var expected = calls.Select(call => BitsFixtures.CompiledOutcome(fixture, call)).ToList();
    var (file, text, _) = this.Translate(true, source, "csharp", "javascript", profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty);
    var node = this.Node(file, profile, calls);
    var mismatches = Enumerable.Range(0, calls.Count).Where(index => node[index] != expected[index]).Select(index => $"{calls[index]}: node {node[index]}, C# {expected[index]}").ToList();
    Assert.That(mismatches.Take(10), Is.Empty, $"{profile}: {mismatches.Count} of {calls.Count} calls differ");
  }

  /// <summary>The levels the fixtures run at: every profile but a script with a global object above ECMAScript 5.</summary>
  private static IEnumerable<ProfileCase> Levels() => ProfileCase.All.Where(profile => profile.Module != "iife" || profile.Level == "es5");

  private static IEnumerable<TestCaseData> CSharpFixturesAndLevels()
    => from fixture in BitsFixtures.CSharp from profile in Levels() select new TestCaseData(fixture, profile);

  private static IEnumerable<TestCaseData> ScriptsAndLevels()
    => from fixture in BitsFixtures.Scripts from profile in Levels() select new TestCaseData(fixture, profile);

  [TestCaseSource(nameof(ScriptsAndLevels))]
  public void JavaScriptFixture_RunsLikeTheOriginal(string fixture, ProfileCase profile) {
    var original = Path.Combine(this._directory, fixture + ".mjs");
    File.Copy(BitsFixtures.ScriptOf(fixture), original);
    var calls = BitsFixtures.ScriptCalls(fixture);
    // node words the range a DataView does not hold its own way; it is the IR's index out of range.
    var expected = Toolchain.NodeOutcomes(this._directory, fixture + ".mjs", calls)
      .Select(outcome => outcome == "trap: Offset is outside the bounds of the DataView" ? "trap: index out of range" : outcome).ToList();
    var module = JavaScriptFixture.Import(fixture + ".mjs", File.ReadAllText(original));
    var interpreted = calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)).ToList();
    Assert.That(Enumerable.Range(0, calls.Count).Where(index => interpreted[index] != expected[index]).Select(index => $"{calls[index]}: interpreter {interpreted[index]}, node {expected[index]}").Take(10), Is.Empty);
    var (file, text, _) = this.Translate(true, fixture + ".mjs", "javascript", "javascript", profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty);
    var node = this.Node(file, profile, calls);
    Assert.That(Enumerable.Range(0, calls.Count).Where(index => node[index] != expected[index]).Select(index => $"{profile}: {calls[index]}: re-emitted {node[index]}, original {expected[index]}").Take(10), Is.Empty);
  }

}
