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
/// <c>polypiler</c> executable: every output is translated by it, every C# output is built by the C#
/// compiler for its profile's framework and for this runtime, every JavaScript output is parsed at its
/// level and run by node, and the interpreter runs the imported IR.
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
  private (string Path, string Text, TimeSpan Took) Translate(string source, string from, string to, params string[] further) {
    var output = Path.Combine(this._directory, "out-" + Guid.NewGuid().ToString("N"));
    var watch = Stopwatch.StartNew();
    var (exitCode, text) = Toolchain.RunPolypiler(this._directory, ["translate", source, "--from", from, "--to", to, "--output", output, .. further]);
    Assert.That(exitCode, Is.Zero, text);
    var file = Directory.GetFiles(output).Single();
    return (file, File.ReadAllText(file), watch.Elapsed);
  }

  /// <summary>What node does with a generated JavaScript file for the calls.</summary>
  private IReadOnlyList<string> Node(string file, ProfileCase profile, IReadOnlyList<PrototypeCall> calls)
    => Toolchain.NodeOutcomes(Path.GetDirectoryName(file)!, Path.GetFileName(file), calls, false, profile.Mode, profile.Global);

  /// <summary>What every C# profile and every JavaScript profile make of a source for the calls: the outcomes by profile.</summary>
  private Dictionary<string, IReadOnlyList<string>> EveryProfile(string source, string from, IReadOnlyList<PrototypeCall> calls) {
    var outcomes = new Dictionary<string, IReadOnlyList<string>>();
    foreach (var profile in CSharpProfile.All) {
      var (_, text, _) = this.Translate(source, from, "csharp", "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
      foreach (var checking in _overflowChecking) {
        var build = GeneratedCSharp.Compile(text, checking);
        outcomes[$"{profile.Name}, CheckOverflow {checking}"] = [.. calls.Select(build.Outcome)];
      }
    }

    foreach (var profile in ProfileCase.All) {
      var (file, text, _) = this.Translate(source, from, "javascript", profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, profile.ToString());
      outcomes[profile.ToString()] = this.Node(file, profile, calls);
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

  /// <summary>Scenario outline: a whole algorithm built on the new operations translates in both directions and every profile.</summary>
  [TestCase("SipHash24.cs", "csharp", "javascript", "11613035633349379557")]
  [TestCase("XxHash32.cs", "csharp", "javascript", "46947589")]
  [TestCase("crc16.js", "javascript", "csharp", "47933")]
  [TestCase("SipHash24.cs", "csharp", "csharp", "11613035633349379557")]
  public void WholeAlgorithm_TranslatesToEveryProfile(string fixture, string from, string to, string result) {
    var source = this.Copy(fixture);
    PrototypeCall call = fixture switch {
      "SipHash24.cs" => new("Hash", [BitsFixtures.Counting(16), BitsFixtures.Counting(15)]) { Returns = typeof(ulong) },
      "XxHash32.cs" => new("Hash", [Array.Empty<byte>(), 0u]),
      _ => new("compute", [Encoding.ASCII.GetBytes("123456789")])
    };
    var outcomes = new List<(string Profile, string Outcome, TimeSpan Took)>();
    if (to == "csharp") {
      foreach (var profile in CSharpProfile.All) {
        var (_, text, took) = this.Translate(source, from, to, "--profile", profile.Name);
        Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
        foreach (var checking in _overflowChecking)
          outcomes.Add(($"{profile.Name}, CheckOverflow {checking}", GeneratedCSharp.Compile(text, checking).Outcome(call), took));
      }
    } else {
      foreach (var profile in ProfileCase.All) {
        var (file, text, took) = this.Translate(source, from, to, profile.Arguments);
        Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, profile.ToString());
        outcomes.Add((profile.ToString(), this.Node(file, profile, [call]).Single(), took));
      }
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
    var outcomes = this.EveryProfile(source, "csharp", [new PrototypeCall("F", [0x12345678u])]);
    Assert.That(outcomes.Where(run => run.Value.Single() != "2018915346").Select(run => $"{run.Key}: {run.Value.Single()}"), Is.Empty);
  }

  /// <summary>Scenario: rotating a 16-bit value by more than its width wraps the count.</summary>
  [Test]
  public void Rotate16_WrapsTheCount() {
    var source = this.Snippet("public static ushort F(ushort x, int n) => ushort.RotateLeft(x, n);");
    var call = new PrototypeCall("F", [(ushort)0x8001, 17]);
    Assert.That(PrototypeFixture.InterpretedOutcome(PrototypeFixture.Import("Snippet.cs", File.ReadAllText(Path.Combine(this._directory, source))), call), Is.EqualTo("3"));
    var outcomes = this.EveryProfile(source, "csharp", [call]);
    Assert.That(outcomes.Where(run => run.Value.Single() != "3").Select(run => $"{run.Key}: {run.Value.Single()}"), Is.Empty);
  }

  /// <summary>Scenario: counting zeros of zero gives the width.</summary>
  [Test]
  public void ZerosOfZero_AreTheWidth() {
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
    var outcomes = this.EveryProfile(source, "csharp", calls);
    Assert.That(outcomes.Where(run => !run.Value.SequenceEqual(widths)).Select(run => $"{run.Key}: {string.Join(", ", run.Value)}"), Is.Empty);
  }

  /// <summary>Scenario: a load outside the array traps before anything happens.</summary>
  [Test]
  public void LoadOutsideTheArray_Traps() {
    var source = this.Snippet("public static uint F(byte[] b, int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i));", true);
    var call = new PrototypeCall("F", [new byte[6], 3]);
    var trap = "trap: index out of range";
    Assert.That(PrototypeFixture.InterpretedOutcome(PrototypeFixture.Import("Snippet.cs", File.ReadAllText(Path.Combine(this._directory, source))), call), Is.EqualTo(trap));
    var outcomes = this.EveryProfile(source, "csharp", [call]);
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
  [TestCaseSource(typeof(BitsFixtures), nameof(BitsFixtures.CSharp))]
  public void CSharpFixture_RunsInNodeAtEveryLevel(string fixture) {
    var source = this.Copy(fixture + ".cs");
    var calls = BitsFixtures.Calls(fixture);
    var expected = calls.Select(call => BitsFixtures.CompiledOutcome(fixture, call)).ToList();
    foreach (var profile in ProfileCase.All.Where(profile => profile.Module != "iife" || profile.Level == "es5")) {
      var (file, text, _) = this.Translate(source, "csharp", "javascript", profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty);
      var node = this.Node(file, profile, calls);
      var mismatches = Enumerable.Range(0, calls.Count).Where(index => node[index] != expected[index]).Select(index => $"{calls[index]}: node {node[index]}, C# {expected[index]}").ToList();
      Assert.That(mismatches.Take(10), Is.Empty, $"{profile}: {mismatches.Count} of {calls.Count} calls differ");
    }
  }

  [TestCaseSource(typeof(BitsFixtures), nameof(BitsFixtures.Scripts))]
  public void JavaScriptFixture_RunsLikeTheOriginal(string fixture) {
    var original = Path.Combine(this._directory, fixture + ".mjs");
    File.Copy(BitsFixtures.ScriptOf(fixture), original);
    var calls = BitsFixtures.ScriptCalls(fixture);
    // node words the range a DataView does not hold its own way; it is the IR's index out of range.
    var expected = Toolchain.NodeOutcomes(this._directory, fixture + ".mjs", calls)
      .Select(outcome => outcome == "trap: Offset is outside the bounds of the DataView" ? "trap: index out of range" : outcome).ToList();
    var module = JavaScriptFixture.Import(fixture + ".mjs", File.ReadAllText(original));
    var interpreted = calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)).ToList();
    Assert.That(Enumerable.Range(0, calls.Count).Where(index => interpreted[index] != expected[index]).Select(index => $"{calls[index]}: interpreter {interpreted[index]}, node {expected[index]}").Take(10), Is.Empty);
    foreach (var profile in ProfileCase.All.Where(profile => profile.Module != "iife" || profile.Level == "es5")) {
      var (file, text, _) = this.Translate(fixture + ".mjs", "javascript", "javascript", profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty);
      var node = this.Node(file, profile, calls);
      Assert.That(Enumerable.Range(0, calls.Count).Where(index => node[index] != expected[index]).Select(index => $"{profile}: {calls[index]}: re-emitted {node[index]}, original {expected[index]}").Take(10), Is.Empty);
    }
  }

}
