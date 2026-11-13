using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The scenarios of <c>tests/features/prototype/ir-to-csharp-wide.feature</c> and the test design of
/// the widened C# backend, with the real <c>polypiler</c> executable. The file it writes is built by
/// the C# compiler — Roslyn, in memory — once with the project's overflow checking off and once with
/// it on, then loaded and called. The expected outcome of a call is never computed here: it is what
/// the original fixture does after the C# compiler built it into this assembly, and what the
/// interpreter does with the imported IR.
/// <para>
/// Every test here starts a process or the compiler, so the fixture is in the <c>EndToEnd</c> tier.
/// </para>
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
public sealed class CSharpWideRoundTripTests {

  private static readonly string[] _scenarios = [
    "A wide fixture survives the round trip C# to IR to C#",
    "A JavaScript checksum becomes C#",
    "A constant table is emitted once as a read-only field",
    "A checked addition stays checked whatever the project says",
    "Float arithmetic is not widened",
    "A for loop with continue keeps its step",
    "An IR node the emitter does not know is refused"
  ];

  private static readonly bool[] _overflowChecking = [false, true];

  private string _directory = "";

  private static IEnumerable<string> WideFixtures() => PrototypeFixture.Names.Except(PrototypeFixture.First);

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(this._directory, "wide"));
    Directory.CreateDirectory(Path.Combine(this._directory, "js"));
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Ensures the feature and its NUnit bindings stay in agreement.</summary>
  [Test]
  public void FeatureScenarios_HaveBindings() {
    var feature = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/features/prototype/ir-to-csharp-wide.feature"));
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario(?: Outline)?: (.+?)\r?$").Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
  }

  /// <summary>Scenario outline: A wide fixture survives the round trip C# to IR to C#.</summary>
  [TestCase("Fnv1a64", "Hash", "a", "12638187200555641996")]
  [TestCase("Crc16", "Compute", "123456789", "10673")]
  [TestCase("Crc32Table", "Compute", "123456789", "3421780262")]
  [TestCase("SplitMix64", "First", null, "16294208416658607535")]
  public void WideFixture_SurvivesTheRoundTrip(string fixture, string method, string? text, string expected) {
    var call = new PrototypeCall(method, [text is null ? 0UL : PrototypeCases.Text(text)]);
    File.Copy(PrototypeFixture.PathOf("wide/" + fixture), Path.Combine(this._directory, "wide", fixture + ".cs"));
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", $"wide/{fixture}.cs", "--from", "csharp", "--to", "csharp", "--output", "out");
    Assert.That(exitCode, Is.Zero, output);

    var generated = File.ReadAllText(Path.Combine(this._directory, "out", fixture + ".cs"));
    var whole = PrototypeFixture.Outcome(null, call.Arguments).Replace("void", expected);
    foreach (var checking in _overflowChecking)
      Assert.That(GeneratedCSharp.Compile(generated, checking).Outcome(call), Is.EqualTo(whole), $"CheckOverflow {checking}");

    Assert.That(PrototypeFixture.CompiledOutcome("wide/" + fixture, call), Is.EqualTo(whole));
  }

  /// <summary>Scenario: A JavaScript checksum becomes C#.</summary>
  [Test]
  public void JavaScriptChecksum_BecomesCSharp() {
    File.Copy(JavaScriptFixture.PathOf("adler32"), Path.Combine(this._directory, "js", "adler32.mjs"));
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", "js/adler32.mjs", "--from", "javascript", "--to", "csharp", "--output", "out");
    Assert.That(exitCode, Is.Zero, output);

    var generated = File.ReadAllText(Path.Combine(this._directory, "out", "adler32.cs"));
    foreach (var checking in _overflowChecking)
      Assert.That(GeneratedCSharp.Compile(generated, checking).Method("compute").Invoke(null, [PrototypeCases.Text("Wikipedia")]), Is.EqualTo(300286872u));
  }

  /// <summary>Scenario: A constant table is emitted once as a read-only field.</summary>
  [Test]
  public void ConstantTable_IsEmittedOnceAsAReadOnlyField() {
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", PrototypeFixture.PathOf("wide/Crc32Table"), "--from", "csharp", "--to", "csharp", "--output", "out");
    Assert.That(exitCode, Is.Zero, output);

    var text = File.ReadAllText(Path.Combine(this._directory, "out", "Crc32Table.cs"));
    var declarations = Regex.Matches(text, @"private static readonly uint\[\] (\w+) = \{([^}]*)\};");
    Assert.That(declarations, Has.Count.EqualTo(1));
    Assert.That(declarations[0].Groups[2].Value.Split(','), Has.Length.EqualTo(256));
    Assert.That(Regex.IsMatch(text, Regex.Escape(declarations[0].Groups[1].Value) + @"\[[^\]]*\]\s*(=[^=]|\+\+|--)"), Is.False);
  }

  /// <summary>Scenario: A checked addition stays checked whatever the project says.</summary>
  [Test]
  public void CheckedAddition_StaysCheckedWhateverTheProjectSays() {
    var method = GeneratedCSharp.Compile(this.TranslateSnippet("public static int F(int a, int b) => checked(a + b);"), checkOverflow: false).Method("F");
    Assert.That(() => method.Invoke(null, [2147483647, 1]), Throws.TypeOf<TargetInvocationException>().With.InnerException.TypeOf<OverflowException>());
  }

  /// <summary>Scenario: Float arithmetic is not widened.</summary>
  [Test]
  public void FloatArithmetic_IsNotWidened() {
    var text = this.TranslateSnippet("public static float F(float a, float b) => a * b + a;");
    var original = (float)PrototypeFixture.Compiled("wide/OperatorsWide", "MultiplyAdd").Invoke(null, [16777217f, 3f])!;
    foreach (var checking in _overflowChecking) {
      var result = (float)GeneratedCSharp.Compile(text, checking).Method("F").Invoke(null, [16777217f, 3f])!;
      Assert.That(BitConverter.SingleToInt32Bits(result), Is.EqualTo(BitConverter.SingleToInt32Bits(original)));
    }
  }

  /// <summary>Scenario: A for loop with continue keeps its step.</summary>
  [Test]
  public void ForLoopWithContinue_KeepsItsStep() {
    var text = this.TranslateSnippet("public static int F(int n) { int s = 0; for (int i = 0; i < n; i++) { if ((i & 1) == 0) continue; s += i; } return s; }");
    foreach (var checking in _overflowChecking)
      Assert.That(GeneratedCSharp.Compile(text, checking).Method("F").Invoke(null, [10]), Is.EqualTo(25));
  }

  private sealed record Riddle : IrStmt;

  /// <summary>Scenario: An IR node the emitter does not know is refused.</summary>
  [Test]
  public void UnknownNode_IsRefused() {
    var module = new IrModule("M", [new IrFunction("F", true, IrType.Void, [], [], new IrBlock([new Riddle()]))]);
    Assert.That(CSharpEmitter.Check(module).Select(problem => problem.Code), Is.EqualTo(new[] { "PLY1052" }));
    Assert.That(() => CSharpEmitter.Emit(module), Throws.ArgumentException);
  }

  /// <summary>
  /// Test design 1 to 6, 8 to 11 and 13: every call of every wide fixture — the published vectors,
  /// each operator and each conversion on each type at its boundaries and on a thousand random
  /// operand tuples (seed 2026), each statement kind, each library operation and each trap — has one
  /// outcome in the file the executable wrote, compiled without and with overflow checking, in the
  /// original fixture and in the interpreter.
  /// </summary>
  [TestCaseSource(nameof(WideFixtures))]
  public void CompiledOutput_Original_AndInterpreter_Agree(string fixture) {
    var calls = PrototypeCases.For(fixture);
    var module = PrototypeFixture.Imported(fixture);
    var output = Path.Combine(this._directory, "out");
    var watch = Stopwatch.StartNew();
    var (exitCode, text) = Toolchain.RunPolypiler(this._directory, "translate", PrototypeFixture.PathOf(fixture), "--from", "csharp", "--to", "csharp", "--output", output);
    TestContext.Out.WriteLine($"{fixture}: translated in {watch.ElapsedMilliseconds} ms");
    Assert.That(exitCode, Is.Zero, text);

    var generated = File.ReadAllText(Path.Combine(output, PrototypeFixture.ClassOf(fixture) + ".cs"));
    var builds = _overflowChecking.Select(checking => GeneratedCSharp.Compile(generated, checking)).ToList();
    var mismatches = new List<string>();
    foreach (var call in calls) {
      var original = PrototypeFixture.CompiledOutcome(fixture, call);
      var interpreted = PrototypeFixture.InterpretedOutcome(module, call);
      var compiled = builds.Select(build => build.Outcome(call)).ToList();
      if (interpreted != original || compiled.Any(outcome => outcome != original))
        mismatches.Add($"{call}: C# {original}, interpreter {interpreted}, generated {string.Join(" / ", compiled)}");
    }

    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls x {builds.Count} builds, {mismatches.Count} mismatches");
    Assert.That(calls, Is.Not.Empty);
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

  /// <summary>Translates one static class of methods with the real executable and returns the generated C#.</summary>
  private string TranslateSnippet(string method) {
    File.WriteAllText(Path.Combine(this._directory, "Snippet.cs"), PrototypeFixture.Wrap(method));
    var (exitCode, text) = Toolchain.RunPolypiler(this._directory, "translate", "Snippet.cs", "--from", "csharp", "--to", "csharp", "--output", "out");
    Assert.That(exitCode, Is.Zero, text);
    return File.ReadAllText(Path.Combine(this._directory, "out", "Snippet.cs"));
  }

}
