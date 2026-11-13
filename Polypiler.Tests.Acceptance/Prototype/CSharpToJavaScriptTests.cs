using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The scenarios of <c>tests/features/prototype/csharp-to-javascript.feature</c> and the test design
/// of the prototype, with the real <c>polypiler</c> executable and the generated module run by
/// <c>node</c>. The expected outcome of a call is never computed here: it is what the same fixture
/// does after the C# compiler built it into this assembly.
/// <para>
/// Every test here starts processes, so the fixture is in the <c>EndToEnd</c> tier: the fast tier on
/// a branch push leaves it out, the pull request runs it on every operating system.
/// </para>
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
public sealed class CSharpToJavaScriptTests {

  private static readonly string[] _scenarios = [
    "A translated checksum matches its published value",
    "Signed arithmetic wraps like C#",
    "An array is modified in place",
    "Division by zero fails the same way in both",
    "An unsupported construct is refused and nothing is written",
    "Invalid C# is reported with the compiler's message",
    "An unknown target language is a usage error"
  ];

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
    var feature = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/features/prototype/csharp-to-javascript.feature"));
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario(?: Outline)?: (.+?)\r?$").Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
  }

  /// <summary>Scenario outline: A translated checksum matches its published value.</summary>
  [TestCase("Adler32", "Compute", "Wikipedia", "300286872")]
  [TestCase("Crc32", "Compute", "123456789", "3421780262")]
  [TestCase("Fnv1a", "Hash", "", "2166136261")]
  [TestCase("Fnv1a", "Hash", "a", "3826002220")]
  public void TranslatedChecksum_MatchesItsPublishedValue(string fixture, string method, string text, string expected)
    => this.TranslatedFixture_Returns(fixture, new PrototypeCall(method, [PrototypeCases.Text(text)]), expected);

  /// <summary>Scenario outline: A translated checksum matches its published value — the rows of Numbers.</summary>
  [TestCase("Gcd", new object[] { 1071, 462 }, "21")]
  [TestCase("Fibonacci", new object[] { 48 }, "512559680")]
  [TestCase("RotateLeft", new object[] { 0x80000001u, 1 }, "3")]
  public void TranslatedNumbers_MatchTheirValue(string method, object[] arguments, string expected)
    => this.TranslatedFixture_Returns("Numbers", new PrototypeCall(method, arguments), expected);

  /// <summary>Scenario outline of the wide feature: A translated algorithm matches its published value.</summary>
  [TestCase("wide/Fnv1a64", "Hash", "a", "12638187200555641996")]
  [TestCase("wide/Fnv1a64", "Hash", "", "14695981039346656037")]
  [TestCase("wide/Crc16", "Compute", "123456789", "10673")]
  public void TranslatedAlgorithm_MatchesItsPublishedValue(string fixture, string method, string text, string expected) {
    var call = new PrototypeCall(method, [PrototypeCases.Text(text)]);
    this.TranslatedFixture_Returns(fixture, call, expected);
    Assert.That(PrototypeFixture.InterpretedOutcome(PrototypeFixture.Imported(fixture), call), Does.StartWith(expected + " | "));
  }

  /// <summary>Scenario outline of the wide feature: the row of SplitMix64, whose argument is a number.</summary>
  [Test]
  public void TranslatedSplitMix64_MatchesItsPublishedValue() {
    var call = new PrototypeCall("First", [0UL]);
    this.TranslatedFixture_Returns("wide/SplitMix64", call, "16294208416658607535");
    Assert.That(PrototypeFixture.InterpretedOutcome(PrototypeFixture.Imported("wide/SplitMix64"), call), Is.EqualTo("16294208416658607535"));
  }

  /// <summary>Scenario: 64-bit arithmetic wraps.</summary>
  [Test]
  public void Arithmetic64Bit_Wraps() {
    const string method = "public static ulong F(ulong a) => a * 0x100000001B3UL + 1UL;";
    Assert.That(this.TranslateSnippet(method, new PrototypeCall("F", [ulong.MaxValue])), Is.EqualTo("18446742974197923406"));
    Assert.That(IrInterpreter.Call(PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(method)), "F", [ulong.MaxValue]), Is.EqualTo(18446742974197923406UL));
  }

  /// <summary>Scenario: Mixed uint and int widens to long as in C#.</summary>
  [Test]
  public void MixedUIntAndInt_WidensToLong() {
    const string method = "public static long F(uint a, int b) => a + b;";
    Assert.That(this.TranslateSnippet(method, new PrototypeCall("F", [4294967295u, -1])), Is.EqualTo("4294967294"));
    Assert.That(IrInterpreter.Call(PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(method)), "F", [4294967295u, -1]), Is.EqualTo(4294967294L));
    Assert.That(File.ReadAllText(Path.Combine(this._directory, "out", "Snippet.mjs")), Does.Contain("// F(a: number, b: number): bigint"));
  }

  /// <summary>Scenario: Float arithmetic is rounded to binary32 after every operation.</summary>
  [Test]
  public void FloatArithmetic_IsRoundedToBinary32AfterEveryOperation() {
    const string method = "public static float F(float a, float b) => a * b + a;";
    var call = new PrototypeCall("F", [16777217f, 3f]);
    var compiled = PrototypeFixture.CompiledOutcome("wide/OperatorsWide", new PrototypeCall("MultiplyAdd", call.Arguments));
    Assert.That(PrototypeFixture.SourceOf("wide/OperatorsWide"), Does.Contain("public static float MultiplyAdd(float a, float b) => a * b + a;"));
    Assert.That(this.TranslateSnippet(method, call), Is.EqualTo(compiled));
    Assert.That(PrototypeFixture.InterpretedOutcome(PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(method)), call), Is.EqualTo(compiled));
    Assert.That(File.ReadAllText(Path.Combine(this._directory, "out", "Snippet.mjs")), Does.Contain("return Math.fround(Math.fround(a * b) + a);"));
  }

  /// <summary>Scenario: A checked addition traps.</summary>
  [Test]
  public void CheckedAddition_Traps() {
    const string method = "public static int F(int a, int b) => checked(a + b);";
    Assert.That(this.TranslateSnippet(method, new PrototypeCall("F", [2147483647, 1])), Is.EqualTo("trap: integer overflow"));
    Assert.That(
      () => IrInterpreter.Call(PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(method)), "F", [2147483647, 1]),
      Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.IntegerOverflow));
  }

  private void TranslatedFixture_Returns(string fixture, PrototypeCall call, string expected) {
    File.Copy(PrototypeFixture.PathOf(fixture), Path.Combine(this._directory, PrototypeFixture.ClassOf(fixture) + ".cs"));
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", PrototypeFixture.ClassOf(fixture) + ".cs", "--from", "csharp", "--to", "javascript", "--output", "out");
    Assert.That(exitCode, Is.Zero, output);
    Assert.That(File.Exists(Path.Combine(this._directory, "out", PrototypeFixture.ClassOf(fixture) + ".mjs")), Is.True);

    var result = Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), PrototypeFixture.ClassOf(fixture) + ".mjs", [call]).Single();
    var unchanged = PrototypeFixture.Outcome(null, call.Arguments)["void".Length..];
    Assert.That(result, Is.EqualTo(expected + unchanged));
    Assert.That(PrototypeFixture.CompiledOutcome(fixture, call), Is.EqualTo(expected + unchanged));
  }

  /// <summary>Scenario: Signed arithmetic wraps like C#.</summary>
  [Test]
  public void SignedArithmetic_WrapsLikeCSharp() {
    const string method = "public static int F(int a, int b) => a * b + 1;";
    var call = new PrototypeCall("F", [65536, 65536]);
    Assert.That(this.TranslateSnippet(method, call), Is.EqualTo("1"));
    Assert.That(IrInterpreter.Call(PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(method)), "F", [65536, 65536]), Is.EqualTo(1));
  }

  /// <summary>Scenario: An array is modified in place.</summary>
  [Test]
  public void Array_IsModifiedInPlace() {
    Assert.That(PrototypeFixture.SourceOf("Arrays"), Does.Contain("public static void Reverse(int[] data)"));
    var outcomes = this.TranslateFixture("Arrays", [new PrototypeCall("Reverse", [new[] { 1, 2, 3, 4, 5 }])]);
    Assert.That(outcomes, Is.EqualTo(new[] { "void | [5,4,3,2,1]" }));
  }

  /// <summary>Scenario: Division by zero fails the same way in both.</summary>
  [Test]
  public void DivisionByZero_FailsTheSameWayInBoth() {
    const string method = "public static int F(int a, int b) => a / b;";
    Assert.That(this.TranslateSnippet(method, new PrototypeCall("F", [1, 0])), Is.EqualTo("trap: divide by zero"));
    Assert.That(
      () => IrInterpreter.Call(PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(method)), "F", [1, 0]),
      Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.DivideByZero));
  }

  /// <summary>Scenario: An unsupported construct is refused and nothing is written.</summary>
  [Test]
  public void UnsupportedConstruct_IsRefusedAndNothingIsWritten() {
    var (exitCode, output) = this.TranslateText(PrototypeFixture.Wrap("public static decimal F(decimal a) => a + 1;"));
    Assert.That(exitCode, Is.EqualTo(3));
    Assert.That(output, Does.Contain("PLY1002").And.Contain("Snippet.cs(1,"));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
    Assert.That(Directory.GetFiles(this._directory, "*", SearchOption.AllDirectories), Is.EqualTo(new[] { Path.Combine(this._directory, "Snippet.cs") }));
  }

  /// <summary>Scenario: Invalid C# is reported with the compiler's message.</summary>
  [Test]
  public void InvalidCSharp_IsReportedWithTheCompilersMessage() {
    var (exitCode, output) = this.TranslateText(PrototypeFixture.Wrap("public static int F( => 1;"));
    Assert.That(exitCode, Is.EqualTo(4));
    Assert.That(output, Does.Contain("PLY1000").And.Match(@"CS\d{4}: "));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
  }

  /// <summary>Scenario: An unknown target language is a usage error.</summary>
  [Test]
  public void UnknownTargetLanguage_IsAUsageError() {
    File.Copy(PrototypeFixture.PathOf("Adler32"), Path.Combine(this._directory, "Adler32.cs"));
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", "Adler32.cs", "--from", "csharp", "--to", "cobol", "--output", "out");
    Assert.That(exitCode, Is.EqualTo(2));
    Assert.That(output, Does.Contain("PLY0001").And.Contain("cobol"));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
  }

  /// <summary>
  /// Test design 1 to 10 and 16: every call of every fixture — the published vectors, each operator on
  /// each type at its boundaries and on a thousand random operand pairs (seed 2026), each statement
  /// kind, each cast and each array access — has one outcome in node, in the interpreter and in C#.
  /// </summary>
  [TestCaseSource(typeof(PrototypeFixture), nameof(PrototypeFixture.Names))]
  public void Node_Interpreter_AndCompiledFixture_Agree(string fixture) {
    var calls = PrototypeCases.For(fixture);
    var module = PrototypeFixture.Imported(fixture);
    var node = this.TranslateFixture(fixture, calls);
    var mismatches = new List<string>();
    for (var index = 0; index < calls.Count; ++index) {
      var compiled = PrototypeFixture.CompiledOutcome(fixture, calls[index]);
      var interpreted = PrototypeFixture.InterpretedOutcome(module, calls[index]);
      if (node[index] != compiled || interpreted != compiled)
        mismatches.Add($"{calls[index]}: C# {compiled}, node {node[index]}, interpreter {interpreted}");
    }

    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

  /// <summary>The tools the evidence of a run names.</summary>
  [Test]
  public void Toolchain_IsReported() {
    var version = Toolchain.NodeVersion();
    TestContext.Out.WriteLine($"node {version}, .NET {Environment.Version}");
    Assert.That(version, Does.Match(@"^v\d+\.\d+\.\d+"));
  }

  /// <summary>Translates a fixture with the real executable and runs the calls through node.</summary>
  private IReadOnlyList<string> TranslateFixture(string fixture, IReadOnlyList<PrototypeCall> calls) {
    var output = Path.Combine(this._directory, "out");
    var watch = Stopwatch.StartNew();
    var (exitCode, text) = Toolchain.RunPolypiler(this._directory, "translate", PrototypeFixture.PathOf(fixture), "--from", "csharp", "--to", "javascript", "--output", output);
    TestContext.Out.WriteLine($"{fixture}: translated in {watch.ElapsedMilliseconds} ms");
    Assert.That(exitCode, Is.Zero, text);
    return Toolchain.NodeOutcomes(output, PrototypeFixture.ClassOf(fixture) + ".mjs", calls);
  }

  private string TranslateSnippet(string method, PrototypeCall call) {
    var (exitCode, text) = this.TranslateText(PrototypeFixture.Wrap(method));
    Assert.That(exitCode, Is.Zero, text);
    return Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), "Snippet.mjs", [call]).Single();
  }

  private (int ExitCode, string Output) TranslateText(string source) {
    File.WriteAllText(Path.Combine(this._directory, "Snippet.cs"), source);
    return Toolchain.RunPolypiler(this._directory, "translate", "Snippet.cs", "--from", "csharp", "--to", "javascript", "--output", "out");
  }

}
