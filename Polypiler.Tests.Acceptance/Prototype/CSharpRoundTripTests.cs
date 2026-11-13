using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The scenarios of <c>tests/features/prototype/ir-to-csharp.feature</c> and the test design of the
/// C# backend, with the real <c>polypiler</c> executable. The file it writes is built by the C#
/// compiler — Roslyn, in memory — once with the project's overflow checking off and once with it on,
/// then loaded and called. The expected outcome of a call is never computed here: it is what the
/// original fixture does after the C# compiler built it into this assembly, and what the interpreter
/// does with the imported IR.
/// <para>
/// Every test here starts a process or the compiler, so the fixture is in the <c>EndToEnd</c> tier.
/// </para>
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
public sealed class CSharpRoundTripTests {

  private static readonly string[] _scenarios = [
    "A fixture survives the round trip C# to IR to C#",
    "Wrap-around does not depend on the project's overflow setting",
    "Byte arithmetic is truncated where the source truncated it",
    "Traps become the matching .NET exceptions",
    "A C# keyword used as an identifier is escaped",
    "Ill-typed IR is refused",
    "The namespace option wraps the class"
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
    var feature = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/features/prototype/ir-to-csharp.feature"));
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario(?: Outline)?: (.+?)\r?$").Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
  }

  /// <summary>Scenario outline: A fixture survives the round trip C# to IR to C# — the checksums.</summary>
  [TestCase("Adler32", "Compute", "Wikipedia", "300286872")]
  [TestCase("Crc32", "Compute", "123456789", "3421780262")]
  [TestCase("Fnv1a", "Hash", "a", "3826002220")]
  public void Checksum_SurvivesTheRoundTrip(string fixture, string method, string text, string expected)
    => this.RoundTrip_Returns(fixture, new PrototypeCall(method, [PrototypeCases.Text(text)]), expected);

  /// <summary>Scenario outline: A fixture survives the round trip C# to IR to C# — the rows of Numbers.</summary>
  [TestCase("Gcd", new object[] { 1071, 462 }, "21")]
  [TestCase("Fibonacci", new object[] { 48 }, "512559680")]
  public void Numbers_SurviveTheRoundTrip(string method, object[] arguments, string expected)
    => this.RoundTrip_Returns("Numbers", new PrototypeCall(method, arguments), expected);

  private void RoundTrip_Returns(string fixture, PrototypeCall call, string expected) {
    File.Copy(PrototypeFixture.PathOf(fixture), Path.Combine(this._directory, fixture + ".cs"));
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", fixture + ".cs", "--from", "csharp", "--to", "csharp", "--output", "out");
    Assert.That(exitCode, Is.Zero, output);
    var generated = Path.Combine(this._directory, "out", fixture + ".cs");
    Assert.That(File.Exists(generated), Is.True);

    var unchanged = PrototypeFixture.Outcome(null, call.Arguments)["void".Length..];
    foreach (var checking in _overflowChecking)
      Assert.That(GeneratedCSharp.Compile(File.ReadAllText(generated), checking).Outcome(call), Is.EqualTo(expected + unchanged), $"CheckOverflow {checking}");

    Assert.That(PrototypeFixture.CompiledOutcome(fixture, call), Is.EqualTo(expected + unchanged));
  }

  /// <summary>Scenario: Wrap-around does not depend on the project's overflow setting.</summary>
  [Test]
  public void WrapAround_DoesNotDependOnTheOverflowSetting() {
    var build = GeneratedCSharp.Compile(this.TranslateSnippet("public static int F(int a, int b) => a * b + 1;"), checkOverflow: true);
    Assert.That(build.Method("F").Invoke(null, [65536, 65536]), Is.EqualTo(1));
  }

  /// <summary>Scenario: Byte arithmetic is truncated where the source truncated it.</summary>
  [Test]
  public void ByteArithmetic_IsTruncatedWhereTheSourceTruncatedIt() {
    var text = this.TranslateSnippet("public static byte F(byte a, byte b) => (byte)(a + b);");
    foreach (var checking in _overflowChecking)
      Assert.That(GeneratedCSharp.Compile(text, checking).Method("F").Invoke(null, [(byte)200, (byte)100]), Is.EqualTo((byte)44));
  }

  /// <summary>Scenario: Traps become the matching .NET exceptions.</summary>
  [Test]
  public void Traps_BecomeTheMatchingExceptions() {
    var text = this.TranslateSnippet("public static int F(int[] d, int i, int b) => d[i] / b;");
    foreach (var checking in _overflowChecking) {
      var method = GeneratedCSharp.Compile(text, checking).Method("F");
      Assert.That(() => method.Invoke(null, [new[] { 1 }, 0, 0]), Throws.TypeOf<TargetInvocationException>().With.InnerException.TypeOf<DivideByZeroException>());
      Assert.That(() => method.Invoke(null, [new[] { 1 }, 1, 1]), Throws.TypeOf<TargetInvocationException>().With.InnerException.TypeOf<IndexOutOfRangeException>());
      Assert.That(() => method.Invoke(null, [new[] { int.MinValue }, 0, -1]), Throws.TypeOf<TargetInvocationException>().With.InnerException.TypeOf<OverflowException>());
    }
  }

  /// <summary>Scenario: A C# keyword used as an identifier is escaped.</summary>
  [Test]
  public void Keyword_UsedAsAnIdentifier_IsEscaped() {
    var parameter = new IrLocal("a", IrType.I32);
    var local = new IrLocal("class", IrType.I32);
    var module = new IrModule("Keywords", [new IrFunction("F", true, IrType.I32, [parameter], [local], new IrBlock([
      new IrDeclare(local, new IrBinary(IrBinaryOp.Add, new IrLocalRef(parameter), new IrConst(IrType.I32, 1))),
      new IrReturn(new IrLocalRef(local))
    ]))]);
    var text = CSharpEmitter.Emit(module).Single().Content;
    Assert.That(text, Does.Contain("int @class = a + 1;"));
    foreach (var checking in _overflowChecking)
      Assert.That(GeneratedCSharp.Compile(text, checking).Method("F").Invoke(null, [41]), Is.EqualTo(42).And.EqualTo(IrInterpreter.Call(module, "F", [41])));
  }

  /// <summary>Scenario: Ill-typed IR is refused.</summary>
  [Test]
  public void IllTypedIr_IsRefused() {
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [], [], new IrBlock([
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrConst(IrType.I32, 1), new IrConst(IrType.Bool, 1)))
    ]))]);
    Assert.That(CSharpEmitter.Check(module).Select(problem => problem.Code), Is.EqualTo(new[] { "PLY0100" }));
    Assert.That(() => CSharpEmitter.Emit(module), Throws.ArgumentException);
  }

  /// <summary>Scenario: The namespace option wraps the class.</summary>
  [Test]
  public void NamespaceOption_WrapsTheClass() {
    var text = this.TranslateSnippet("public static int F() => 1;", "--namespace", "Demo.Checksums");
    Assert.That(text.Split('\n').First(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal)), Is.EqualTo("namespace Demo.Checksums;"));
    Assert.That(GeneratedCSharp.Compile(text, false).ClassName, Is.EqualTo("Demo.Checksums.Snippet"));
  }

  /// <summary>Test design 10, with the real executable: a usage error is exit code 2 and writes nothing.</summary>
  [TestCase(new[] { "--to", "cobol", "--output", "out" }, "unknown target language 'cobol'")]
  [TestCase(new[] { "--to", "csharp" }, "the option '--output' is missing")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--namespace", "2fast" }, "'2fast' is not a namespace name")]
  public void Misuse_IsAUsageError(string[] arguments, string message) {
    File.Copy(PrototypeFixture.PathOf("Adler32"), Path.Combine(this._directory, "Adler32.cs"));
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, ["translate", "Adler32.cs", "--from", "csharp", .. arguments]);
    Assert.That(exitCode, Is.EqualTo(2));
    Assert.That(output, Does.Contain("error PLY0001: " + message));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
  }

  /// <summary>
  /// Test design 1 to 8 and 12: every call of every fixture — the published vectors, each operator on
  /// each type at its boundaries and on a thousand random operand tuples (seed 2026), each statement
  /// kind, each cast, each array access and each trap — has one outcome in the file the executable
  /// wrote, compiled without and with overflow checking, in the original fixture and in the interpreter.
  /// </summary>
  [TestCaseSource(typeof(PrototypeFixture), nameof(PrototypeFixture.First))]
  public void CompiledOutput_Original_AndInterpreter_Agree(string fixture) {
    var calls = PrototypeCases.For(fixture);
    var module = PrototypeFixture.Import(fixture + ".cs", PrototypeFixture.SourceOf(fixture));
    var output = Path.Combine(this._directory, "out");
    var watch = Stopwatch.StartNew();
    var (exitCode, text) = Toolchain.RunPolypiler(this._directory, "translate", PrototypeFixture.PathOf(fixture), "--from", "csharp", "--to", "csharp", "--output", output);
    TestContext.Out.WriteLine($"{fixture}: translated in {watch.ElapsedMilliseconds} ms");
    Assert.That(exitCode, Is.Zero, text);

    var generated = File.ReadAllText(Path.Combine(output, fixture + ".cs"));
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

  /// <summary>The tools the evidence of a run names.</summary>
  [Test]
  public void Toolchain_IsReported() {
    var compiler = typeof(CSharpCompilation).Assembly.GetName().Version;
    TestContext.Out.WriteLine($"Roslyn {compiler}, .NET {Environment.Version}");
    Assert.That(compiler, Is.Not.Null);
  }

  /// <summary>Translates one static class of methods with the real executable and returns the generated C#.</summary>
  private string TranslateSnippet(string method, params string[] options) {
    File.WriteAllText(Path.Combine(this._directory, "Snippet.cs"), PrototypeFixture.Wrap(method));
    var (exitCode, text) = Toolchain.RunPolypiler(this._directory, ["translate", "Snippet.cs", "--from", "csharp", "--to", "csharp", "--output", "out", .. options]);
    Assert.That(exitCode, Is.Zero, text);
    return File.ReadAllText(Path.Combine(this._directory, "out", "Snippet.cs"));
  }

}
