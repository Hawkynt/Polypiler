using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

  private static readonly string[] _scenarios = [
    "A whole algorithm built on the new operations translates in both directions and every profile",
    "A byte swap is a library call where the library has it",
    "Rotating a 16-bit value by more than its width wraps the count",
    "Counting zeros of zero gives the width",
    "A load outside the array traps before anything happens",
    "An unmapped member is refused by name"
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
    var feature = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/features/prototype/integer-operations.feature"));
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario(?: Outline)?: (.+?)\r?$").Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
  }

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

  /// <summary>What one C# or JavaScript profile makes of a source for the calls, with the real executable: the outcomes by build.</summary>
  private Dictionary<string, IReadOnlyList<string>> OneProfile(string source, string from, TargetCase target, IReadOnlyList<PrototypeCall> calls) {
    var outcomes = new Dictionary<string, IReadOnlyList<string>>();
    if (target.CSharp is { } profile) {
      var (_, text, _) = this.Translate(source, from, "csharp", "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
      foreach (var checking in _overflowChecking) {
        var build = GeneratedCSharp.Compile(text, checking);
        outcomes[$"{profile.Name}, CheckOverflow {checking}"] = [.. calls.Select(build.Outcome)];
      }
    } else {
      var script = target.JavaScript!;
      var (file, text, _) = this.Translate(source, from, "javascript", script.Arguments);
      Assert.That(GeneratedJavaScript.Problems(text, script.Parsed), Is.Empty, script.ToString());
      outcomes[script.ToString()] = this.Node(file, script, calls);
    }

    return outcomes;
  }

  private string Snippet(string method, bool system = false) {
    File.WriteAllText(Path.Combine(this._directory, "Snippet.cs"), (system ? "using System; " : "") + PrototypeFixture.Wrap(method));
    return "Snippet.cs";
  }

  private string Copy(string fixture) {
    var name = Path.GetFileName(fixture);
    File.Copy(Path.Combine(BitsFixtures.Directory, name), Path.Combine(this._directory, name));
    return name;
  }

  /// <summary>The examples of the scenario outline, one case per profile of the target language.</summary>
  private static IEnumerable<TestCaseData> WholeAlgorithms() =>
    from example in new[] {
      (Fixture: "SipHash24.cs", From: "csharp", To: "javascript", Result: "11613035633349379557"),
      (Fixture: "XxHash32.cs", From: "csharp", To: "javascript", Result: "46947589"),
      (Fixture: "crc16.js", From: "javascript", To: "csharp", Result: "47933"),
      (Fixture: "SipHash24.cs", From: "csharp", To: "csharp", Result: "11613035633349379557")
    }
    from target in example.To == "csharp" ? TargetCase.CSharpProfiles : TargetCase.JavaScriptProfiles
    select new TestCaseData(example.Fixture, example.From, example.To, example.Result, target);

  /// <summary>Scenario outline: a whole algorithm built on the new operations translates in both directions and every profile.</summary>
  [TestCaseSource(nameof(WholeAlgorithms))]
  public void WholeAlgorithm_TranslatesToEveryProfile(string fixture, string from, string to, string result, TargetCase target) {
    var source = this.Copy(fixture);
    PrototypeCall call = fixture switch {
      "SipHash24.cs" => new("Hash", [BitsFixtures.Counting(16), BitsFixtures.Counting(15)]) { Returns = typeof(ulong) },
      "XxHash32.cs" => new("Hash", [Array.Empty<byte>(), 0u]),
      _ => new("compute", [Encoding.ASCII.GetBytes("123456789")])
    };
    var outcomes = new List<(string Profile, string Outcome, TimeSpan Took)>();
    Assert.That(target.Language, Is.EqualTo(to));
    if (target.CSharp is { } profile) {
      var (_, text, took) = this.Translate(source, from, to, "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
      foreach (var checking in _overflowChecking)
        outcomes.Add(($"{profile.Name}, CheckOverflow {checking}", GeneratedCSharp.Compile(text, checking).Outcome(call), took));
    } else {
      var script = target.JavaScript!;
      var (file, text, took) = this.Translate(source, from, to, script.Arguments);
      Assert.That(GeneratedJavaScript.Problems(text, script.Parsed), Is.Empty, script.ToString());
      outcomes.Add((script.ToString(), this.Node(file, script, [call]).Single(), took));
    }

    TestContext.Out.WriteLine($"{fixture} to {to}: {outcomes.Count} runs, translated in {outcomes.Min(run => run.Took.TotalMilliseconds):0} to {outcomes.Max(run => run.Took.TotalMilliseconds):0} ms");
    Assert.That(outcomes.Where(run => !run.Outcome.StartsWith(result + " | ", StringComparison.Ordinal)).Select(run => $"{run.Profile}: {run.Outcome}"), Is.Empty);
  }

  /// <summary>Scenario: a byte swap is a library call where the library has it.</summary>
  [Test]
  public void ByteSwap_IsALibraryCallWhereTheLibraryHasIt() {
    var source = this.Snippet("public static uint F(uint x) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(x);");
    Assert.That(this.Translate(source, "csharp", "csharp", "--profile", "net10.0").Text, Does.Contain("BinaryPrimitives.ReverseEndianness"));
    Assert.That(this.Translate(source, "csharp", "csharp", "--profile", "net2.0").Text, Does.Contain("private static ulong ByteSwapBits(").And.Not.Contain("BinaryPrimitives"));
    Assert.That(this.Translate(source, "csharp", "javascript", "--profile", "es5").Text, Does.Contain("function $bswap("));
  }

  /// <summary>Scenario: a byte swap is a library call where the library has it - and computes the same in every profile.</summary>
  [TestCaseSource(typeof(TargetCase), nameof(TargetCase.All))]
  public void ByteSwap_IsALibraryCallWhereTheLibraryHasIt_InEveryProfile(TargetCase target) {
    var source = this.Snippet("public static uint F(uint x) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(x);");
    var outcomes = this.OneProfile(source, "csharp", target, [new PrototypeCall("F", [0x12345678u])]);
    Assert.That(outcomes.Where(run => run.Value.Single() != "2018915346").Select(run => $"{run.Key}: {run.Value.Single()}"), Is.Empty);
  }

  /// <summary>Scenario: rotating a 16-bit value by more than its width wraps the count.</summary>
  [TestCaseSource(typeof(TargetCase), nameof(TargetCase.All))]
  public void Rotate16_WrapsTheCount(TargetCase target) {
    var source = this.Snippet("public static ushort F(ushort x, int n) => ushort.RotateLeft(x, n);");
    var call = new PrototypeCall("F", [(ushort)0x8001, 17]);
    Assert.That(PrototypeFixture.InterpretedOutcome(PrototypeFixture.Import("Snippet.cs", File.ReadAllText(Path.Combine(this._directory, source))), call), Is.EqualTo("3"));
    var outcomes = this.OneProfile(source, "csharp", target, [call]);
    Assert.That(outcomes.Where(run => run.Value.Single() != "3").Select(run => $"{run.Key}: {run.Value.Single()}"), Is.Empty);
  }

  /// <summary>Scenario: counting zeros of zero gives the width.</summary>
  [TestCaseSource(typeof(TargetCase), nameof(TargetCase.All))]
  public void ZerosOfZero_AreTheWidth(TargetCase target) {
    var source = this.Snippet(string.Join(" ",
      "public static byte L8(byte x) => byte.LeadingZeroCount(x); public static ushort L16(ushort x) => ushort.LeadingZeroCount(x);",
      "public static int L32(uint x) => System.Numerics.BitOperations.LeadingZeroCount(x); public static int L64(ulong x) => System.Numerics.BitOperations.LeadingZeroCount(x);",
      "public static byte T8(byte x) => byte.TrailingZeroCount(x); public static ushort T16(ushort x) => ushort.TrailingZeroCount(x);",
      "public static int T32(uint x) => System.Numerics.BitOperations.TrailingZeroCount(x); public static int T64(ulong x) => System.Numerics.BitOperations.TrailingZeroCount(x);"));
    PrototypeCall[] calls = [
      new("L8", [(byte)0]), new("L16", [(ushort)0]), new("L32", [0u]), new("L64", [0UL]),
      new("T8", [(byte)0]), new("T16", [(ushort)0]), new("T32", [0u]), new("T64", [0UL])
    ];
    string[] widths = ["8", "16", "32", "64", "8", "16", "32", "64"];
    var outcomes = this.OneProfile(source, "csharp", target, calls);
    Assert.That(outcomes.Where(run => !run.Value.SequenceEqual(widths)).Select(run => $"{run.Key}: {string.Join(", ", run.Value)}"), Is.Empty);
  }

  /// <summary>Scenario: a load outside the array traps before anything happens.</summary>
  [TestCaseSource(typeof(TargetCase), nameof(TargetCase.All))]
  public void LoadOutsideTheArray_Traps(TargetCase target) {
    var source = this.Snippet("public static uint F(byte[] b, int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i));", true);
    var call = new PrototypeCall("F", [new byte[6], 3]);
    var trap = "trap: index out of range";
    Assert.That(PrototypeFixture.InterpretedOutcome(PrototypeFixture.Import("Snippet.cs", File.ReadAllText(Path.Combine(this._directory, source))), call), Is.EqualTo(trap));
    var outcomes = this.OneProfile(source, "csharp", target, [call]);
    Assert.That(outcomes.Where(run => run.Value.Single() != trap).Select(run => $"{run.Key}: {run.Value.Single()}"), Is.Empty);
  }

  /// <summary>
  /// Scenario: an unmapped member is refused by name. The issue named the code PLY1003, which is the
  /// C# frontend's code for a constant that overflows (exit code 4); a construct outside the subset is
  /// PLY1002 with exit code 3, which is what the scenario asks for.
  /// </summary>
  [Test]
  public void UnmappedMember_IsRefusedByName() {
    var source = this.Snippet("public static int F(int x) => System.Numerics.BitOperations.Crc32C(0u, (uint)x) > 0 ? 1 : 0;");
    var output = Path.Combine(this._directory, "out");
    var (exitCode, text) = Toolchain.RunPolypiler(this._directory, "translate", source, "--from", "csharp", "--to", "javascript", "--output", output);
    Assert.That(exitCode, Is.EqualTo(3));
    Assert.That(text, Does.Contain("PLY1002").And.Contain("Crc32C"));
    Assert.That(Directory.Exists(output), Is.False);
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
