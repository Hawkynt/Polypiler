using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Optimize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The scenarios of <c>tests/features/prototype/recognition.feature</c> with the real <c>polypiler</c>
/// executable, every C# output built for its framework and for this runtime, every JavaScript output
/// parsed at its level and run by node; and the spelled-out SipHash and xxHash in every profile of
/// both backends with optimization on and off (test design 3).
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class RecognitionTests {

  private static readonly string[] _scenarios = [
    "A rotate written as two shifts becomes the library call",
    "Byte loads joined by shifts become one endian load",
    "A spelled-out xxHash32 recovers its rotates and loads",
    "A branchless absolute value does not become a trapping one",
    "A near-miss is left alone",
    "An unknown optimization id is a usage error"
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
    var feature = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/features/prototype/recognition.feature"));
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario(?: Outline)?: (.+?)\r?$").Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
  }

  /// <summary>Translates a file of the working directory with the real executable: the file written, its text, everything the command printed and how long it took.</summary>
  private (string Path, string Text, string Printed, TimeSpan Took) Translate(string source, string from, string to, params string[] further) {
    var output = Path.Combine(this._directory, "out-" + Guid.NewGuid().ToString("N"));
    var watch = Stopwatch.StartNew();
    var (exitCode, printed) = Toolchain.RunPolypiler(this._directory, ["translate", source, "--from", from, "--to", to, "--output", output, .. further]);
    Assert.That(exitCode, Is.Zero, printed);
    var file = Directory.GetFiles(output).Single();
    return (file, File.ReadAllText(file), printed, watch.Elapsed);
  }

  private string Snippet(string method) {
    File.WriteAllText(Path.Combine(this._directory, "Snippet.cs"), PrototypeFixture.Wrap(method));
    return "Snippet.cs";
  }

  private string Copy(string fixture) {
    File.Copy(Path.Combine(RecognitionFixtures.Directory, fixture), Path.Combine(this._directory, fixture));
    return fixture;
  }

  /// <summary>What every C# profile and every JavaScript profile make of a source for the calls, by profile.</summary>
  private Dictionary<string, IReadOnlyList<string>> EveryProfile(string source, IReadOnlyList<PrototypeCall> calls, params string[] further) {
    var outcomes = new Dictionary<string, IReadOnlyList<string>>();
    var took = new List<TimeSpan>();
    foreach (var profile in CSharpProfile.All) {
      var (_, text, _, time) = this.Translate(source, "csharp", "csharp", ["--profile", profile.Name, .. further]);
      took.Add(time);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
      foreach (var checking in _overflowChecking)
        outcomes[$"{profile.Name}, CheckOverflow {checking}"] = [.. calls.Select(GeneratedCSharp.Compile(text, checking).Outcome)];
    }

    foreach (var profile in ProfileCase.All) {
      var (file, text, _, time) = this.Translate(source, "csharp", "javascript", [.. profile.Arguments, .. further]);
      took.Add(time);
      Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, profile.ToString());
      outcomes[profile.ToString()] = Toolchain.NodeOutcomes(Path.GetDirectoryName(file)!, Path.GetFileName(file), calls, false, profile.Mode, profile.Global);
    }

    TestContext.Out.WriteLine($"{source} {string.Join(" ", further)}: {outcomes.Count} runs, translated in {took.Min().TotalMilliseconds:0} to {took.Max().TotalMilliseconds:0} ms");
    return outcomes;
  }

  /// <summary>Scenario: a rotate written as two shifts becomes the library call.</summary>
  [Test]
  public void Rotate_BecomesTheLibraryCall() {
    var source = this.Snippet("public static uint F(uint x) => (x << 8) | (x >> 24);");
    var optimized = this.Translate(source, "csharp", "csharp", "--profile", "net10.0").Text;
    Assert.That(optimized, Does.Contain("BitOperations.RotateLeft").And.Not.Contain("<< 8"));
    Assert.That(GeneratedCSharp.Compile(optimized, false).Outcome(new PrototypeCall("F", [0x12345678u])), Is.EqualTo("878082066"));
    var disabled = this.Translate(source, "csharp", "csharp", "--profile", "net10.0", "--disable-opt", "O0100").Text;
    Assert.That(disabled, Does.Contain("<< 8").And.Not.Contain("RotateLeft"));
  }

  /// <summary>Scenario: byte loads joined by shifts become one endian load.</summary>
  [Test]
  public void ByteLoads_BecomeOneEndianLoad() {
    var source = this.Snippet("public static uint F(byte[] b, int i) => (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24);");
    var text = this.Translate(source, "csharp", "csharp", "--profile", "net10.0").Text;
    Assert.That(text, Does.Contain("BinaryPrimitives.ReadUInt32LittleEndian"));
    Assert.That(GeneratedCSharp.Compile(text, false).Outcome(new PrototypeCall("F", [new byte[] { 0x78, 0x56, 0x34, 0x12 }, 0])), Does.StartWith("305419896 | "));
  }

  /// <summary>Scenario: a spelled-out xxHash32 recovers its rotates and loads.</summary>
  [Test]
  public void SpelledXxHash_RecoversItsRotatesAndLoads() {
    var source = this.Copy("XxHash32Spelled.cs");
    var call = new PrototypeCall("Hash", [Array.Empty<byte>(), 0u]);
    foreach (var level in new[] { "default", "none" }) {
      var (file, _, printed, _) = this.Translate(source, "csharp", "javascript", "--profile", "es2022", "--explain-opt", "--opt", level);
      var applied = printed.Split('\n').Where(line => line.Contains(": opt ", StringComparison.Ordinal)).ToList();
      if (level == "default") {
        Assert.That(applied.Count(line => line.Contains(" O0100 ", StringComparison.Ordinal)), Is.GreaterThanOrEqualTo(5));
        Assert.That(applied.Count(line => line.Contains(" O0140 ", StringComparison.Ordinal)), Is.GreaterThanOrEqualTo(1));
      } else {
        Assert.That(applied, Is.Empty);
      }

      Assert.That(Toolchain.NodeOutcomes(Path.GetDirectoryName(file)!, Path.GetFileName(file), [call]).Single(), Is.EqualTo("46947589 | []"), level);
    }
  }

  /// <summary>Scenario: a branchless absolute value does not become a trapping one.</summary>
  [Test]
  public void BranchlessAbs_DoesNotTrap() {
    const string method = "public static int F(int x) { int m = x >> 31; return (x ^ m) - m; }";
    var source = this.Snippet(method);
    var ir = IrText.Of(Optimizer.Run(PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(method)), new OptimizerOptions()).Module);
    Assert.That(ir, Does.Contain("AbsWrap").And.Not.Contain("Abs("));
    var outcomes = this.EveryProfile(source, [new PrototypeCall("F", [int.MinValue])]);
    Assert.That(outcomes.Where(run => run.Value.Single() != "-2147483648").Select(run => $"{run.Key}: {run.Value.Single()}"), Is.Empty);
  }

  /// <summary>Scenario: a near-miss is left alone.</summary>
  [Test]
  public void NearMiss_IsLeftAlone() {
    var source = this.Snippet("public static uint F(uint x) => (x << 8) | (x >> 23);");
    var (_, text, printed, _) = this.Translate(source, "csharp", "csharp", "--profile", "net10.0", "--explain-opt");
    Assert.That(printed, Does.Not.Contain(": opt "));
    Assert.That(text, Does.Contain("<< 8").And.Contain(">> 23"));
  }

  /// <summary>Scenario: an unknown optimization id is a usage error.</summary>
  [Test]
  public void UnknownOptimizationId_IsAUsageError() {
    var source = this.Snippet("public static int F(int x) => x;");
    var (exitCode, printed) = Toolchain.RunPolypiler(this._directory, "translate", source, "--from", "csharp", "--to", "javascript", "--output", "out", "--disable-opt", "O9999x");
    Assert.That(exitCode, Is.EqualTo(2));
    Assert.That(printed, Does.Contain("PLY0001"));
  }

  /// <summary>
  /// Test design 3: the spelled-out SipHash-2-4 and xxHash32 in every profile of both backends, with
  /// optimization on and off, give what the library-call versions give as the C# compiler built
  /// them - the published vectors among the calls - and the rules apply at least as often as the
  /// library-call versions call the library.
  /// </summary>
  [TestCase("SipHash24Spelled.cs", "SipHash24", 18, 3)]
  [TestCase("XxHash32Spelled.cs", "XxHash32", 10, 5)]
  public void SpelledAlgorithm_ComputesItsVectorsInEveryProfile(string fixture, string library, int rotates, int loads) {
    var source = this.Copy(fixture);
    var calls = library == "SipHash24"
      ? new PrototypeCall[] {
        new("Hash", [BitsFixtures.Counting(16), BitsFixtures.Counting(15)]) { Returns = typeof(ulong) },
        new("Hash", [BitsFixtures.Counting(16), Array.Empty<byte>()]) { Returns = typeof(ulong) },
        new("Hash", [BitsFixtures.Counting(16), BitsFixtures.Counting(63)]) { Returns = typeof(ulong) },
        new("Hash", [BitsFixtures.Counting(15), BitsFixtures.Counting(4)]) { Returns = typeof(ulong) }
      }
      : [
        new("Hash", [Array.Empty<byte>(), 0u]),
        new("Hash", [Encoding.ASCII.GetBytes("abc"), 0u]),
        new("Hash", [Encoding.ASCII.GetBytes("Nobody inspects the spammish repetition"), 0u]),
        new("Hash", [BitsFixtures.Counting(37), 2654435761u])
      ];
    var expected = calls.Select(call => BitsFixtures.CompiledOutcome(library, call)).ToList();
    var printed = this.Translate(source, "csharp", "javascript", "--explain-opt").Printed;
    Assert.That(Regex.Matches(printed, " O0100 ").Count, Is.GreaterThanOrEqualTo(rotates));
    Assert.That(Regex.Matches(printed, " O0140 ").Count, Is.GreaterThanOrEqualTo(loads));
    foreach (var level in new[] { "default", "none" }) {
      var outcomes = this.EveryProfile(source, calls, "--opt", level);
      Assert.That(outcomes.Where(run => !run.Value.SequenceEqual(expected)).Select(run => $"{level}, {run.Key}: {string.Join("; ", run.Value)}"), Is.Empty);
    }
  }

}
