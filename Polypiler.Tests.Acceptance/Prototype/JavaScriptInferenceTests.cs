using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The scenarios of <c>tests/features/prototype/javascript-inference.feature</c>, with the real
/// <c>polypiler</c> executable and <c>node</c>. An expected outcome is what <c>node</c> does with the
/// original source; the interpreter and the module emitted from the IR are held to it, a double by
/// its bits.
/// <para>
/// Every test here starts processes, so the fixture is in the <c>EndToEnd</c> tier: the fast tier on
/// a branch push leaves it out, the pull request runs it on every operating system.
/// </para>
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
public sealed class JavaScriptInferenceTests {

  private static readonly string[] _scenarios = [
    "An unannotated checksum is imported with integer types",
    "A parameter used only under coercion is an integer, and the caveat says so",
    "A parameter used in plain arithmetic stays a double",
    "A coerced product is not narrowed to an integer multiplication",
    "Math.imul is an integer multiplication",
    "An unbounded BigInt is refused, a bounded one is a 64-bit integer",
    "A variable holding a number and an array is refused",
    "A switch section that falls through into statements is refused"
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
    var feature = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/features/prototype/javascript-inference.feature"));
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario(?: Outline)?: (.+?)\r?$").Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
  }

  /// <summary>Scenario outline: An unannotated checksum is imported with integer types.</summary>
  [TestCase("adler32", "compute", "a", "U32", "Wikipedia", "300286872")]
  [TestCase("crc32", "compute", "crc", "U32", "123456789", "3421780262")]
  [TestCase("fnv1a", "hash", "h", "U32", "a", "3826002220")]
  public void UnannotatedChecksum_IsImportedWithIntegerTypes(string fixture, string function, string local, string type, string text, string expected) {
    var name = "untyped/" + fixture;
    Assert.That(JavaScriptFixture.SourceOf(name), Does.Not.Contain("/**").And.Not.Contain("@param"));
    File.Copy(JavaScriptFixture.PathOf(name), Path.Combine(this._directory, fixture + ".mjs"));

    var watch = Stopwatch.StartNew();
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", fixture + ".mjs", "--from", "javascript", "--to", "javascript", "--output", "out", "--explain-types");
    TestContext.Out.WriteLine($"{name}: translated in {watch.ElapsedMilliseconds} ms");
    Assert.That(exitCode, Is.Zero, output);
    Assert.That(output, Does.Contain($"{fixture}.mjs: type {function}: local {local}: {type.ToLowerInvariant()} from '"));

    var result = JavaScriptFrontend.Import(fixture + ".mjs", JavaScriptFixture.SourceOf(name));
    var explained = result.Explanations.Single(explanation => (explanation.Function, explanation.Role, explanation.Name) == (function, "local", local));
    Assert.That(explained.Type.ToString(), Is.EqualTo(type.ToLowerInvariant()));

    var call = new PrototypeCall(function, [PrototypeCases.Text(text)]);
    var outcome = expected + PrototypeFixture.Outcome(null, call.Arguments)["void".Length..];
    Assert.That(Toolchain.NodeOutcomes(this._directory, fixture + ".mjs", [call], true).Single(), Is.EqualTo(outcome));
    Assert.That(PrototypeFixture.InterpretedOutcome(result.Module!, call), Is.EqualTo(outcome));
    Assert.That(Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), fixture + ".mjs", [call], true).Single(), Is.EqualTo(outcome));
  }

  /// <summary>Scenario: A parameter used only under coercion is an integer, and the caveat says so.</summary>
  [Test]
  public void ParameterUsedOnlyUnderCoercion_IsAnInteger_AndTheCaveatSaysSo() {
    const string source = "export function f(a, b) { return (a ^ b) >>> 0; }";
    var result = JavaScriptFrontend.Import("snippet.mjs", source);
    var function = result.Module!.Functions.Single();
    Assert.That(function.Parameters.Select(parameter => (parameter.Name, parameter.Type)), Is.EqualTo(new[] { ("a", IrType.I32), ("b", IrType.I32) }));
    Assert.That(function.ReturnType, Is.EqualTo(IrType.U32));

    var (exitCode, output) = this.Translate(source);
    Assert.That(exitCode, Is.Zero, output);
    Assert.That(output, Does.Contain("snippet.mjs: note js-param-integer-inferred:").And.Contain(": f(a, b)"));

    var call = new PrototypeCall("f", [-1, 1]);
    Assert.That(this.Everywhere(source, call), Is.EqualTo(new[] { "4294967294", "4294967294", "4294967294" }));
  }

  /// <summary>Scenario: A parameter used in plain arithmetic stays a double.</summary>
  [Test]
  public void ParameterUsedInPlainArithmetic_StaysADouble() {
    const string source = "export function f(a, b) { return a + b; }";
    var function = JavaScriptFixture.Import("snippet.mjs", source).Functions.Single();
    Assert.That(function.Parameters.Select(parameter => parameter.Type).Append(function.ReturnType), Has.All.EqualTo(IrType.F64));

    var call = new PrototypeCall("f", [0.1, 0.2]);
    Assert.That(this.Everywhere(source, call), Has.All.EqualTo("f:3fd3333333333334"));
    Assert.That(BitConverter.DoubleToInt64Bits((double)IrInterpreter.Call(JavaScriptFixture.Import("snippet.mjs", source), "f", [0.1, 0.2])!), Is.EqualTo(0x3FD3333333333334));
  }

  /// <summary>Scenario: A coerced product is not narrowed to an integer multiplication.</summary>
  [Test]
  public void CoercedProduct_IsNotNarrowedToAnIntegerMultiplication() {
    const string source = "export function f(a, b) { a = a | 0; b = b | 0; return (a * b) | 0; }";
    var call = new PrototypeCall("f", [2147483647, 2147483647]);
    Assert.That(this.Everywhere(source, call), Has.All.EqualTo("0"));

    var returned = (IrIntrinsic)((IrReturn)JavaScriptFixture.Import("snippet.mjs", source).Functions.Single().Body.Statements.Single()).Value!;
    Assert.That(returned.Op, Is.EqualTo(IrIntrinsicOp.ToInt32));
    Assert.That(returned.Arguments.Single(), Is.InstanceOf<IrBinary>().With.Property(nameof(IrBinary.Op)).EqualTo(IrBinaryOp.Multiply).And.Property(nameof(IrExpr.Type)).EqualTo(IrType.F64));
  }

  /// <summary>Scenario: Math.imul is an integer multiplication.</summary>
  [Test]
  public void MathImul_IsAnIntegerMultiplication() {
    const string source = "export function f(a, b) { return Math.imul(a, b); }";
    Assert.That(this.Everywhere(source, new PrototypeCall("f", [2147483647, 2147483647])), Has.All.EqualTo("1"));
  }

  /// <summary>Scenario: An unbounded BigInt is refused, a bounded one is a 64-bit integer.</summary>
  [Test]
  public void UnboundedBigInt_IsRefused_ABoundedOneIsA64BitInteger() {
    var (exitCode, output) = this.Translate("export function f(a) { return a * 1099511628211n; }");
    Assert.That(exitCode, Is.EqualTo(3));
    Assert.That(output, Does.Contain("snippet.mjs(1,31): error PLY1103: the result of 'a * 1099511628211n' has no bounded type"));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);

    const string bounded = "/** @param {uint64} a */ export function f(a) { return BigInt.asUintN(64, a * 1099511628211n); }";
    Assert.That(JavaScriptFixture.Import("snippet.mjs", bounded).Functions.Single().ReturnType, Is.EqualTo(IrType.U64));
    Assert.That(this.Everywhere(bounded, new PrototypeCall("f", [14695981039346656037UL])), Has.All.EqualTo("12638153115695167455"));
  }

  /// <summary>Scenario: A variable holding a number and an array is refused.</summary>
  [Test]
  public void VariableHoldingANumberAndAnArray_IsRefused() {
    var (exitCode, output) = this.Translate("export function f(n) { let x = 0; if (n > 1) x = new Uint8Array(n); return x; }");
    Assert.That(exitCode, Is.EqualTo(3));
    Assert.That(output, Does.Contain("snippet.mjs(1,50): error PLY1105: 'x' holds both number and uint8[]"));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
  }

  /// <summary>Scenario: A switch section that falls through into statements is refused.</summary>
  [Test]
  public void SwitchSectionThatFallsThroughIntoStatements_IsRefused() {
    var (exitCode, output) = this.Translate("export function f(n) {\n  let x = 0;\n  switch (n | 0) {\n    case 1:\n      x = 1;\n    case 2:\n      x = 2;\n      break;\n  }\n  return x;\n}");
    Assert.That(exitCode, Is.EqualTo(3));
    Assert.That(output, Does.Contain("snippet.mjs(4,5): error PLY1102: 'a switch section that falls through into the next' is not supported yet"));
  }

  /// <summary>The evidence of the issue: one success with its explanations and one refusal, as the command prints them.</summary>
  [Test]
  public void Transcript_OfOneSuccessAndOneRefusal() {
    File.Copy(JavaScriptFixture.PathOf("untyped/fnv1a64"), Path.Combine(this._directory, "fnv1a64.mjs"));
    var success = Toolchain.RunPolypiler(this._directory, "translate", "fnv1a64.mjs", "--from", "javascript", "--to", "javascript", "--output", "out", "--explain-types");
    var refusal = this.Translate("export function f(a) { return a * 1099511628211n; }");
    TestContext.Out.WriteLine($"node {Toolchain.NodeVersion()}");
    TestContext.Out.WriteLine($"$ polypiler translate fnv1a64.mjs --from javascript --to javascript --output out --explain-types (exit code {success.ExitCode})");
    TestContext.Out.WriteLine(success.Output);
    TestContext.Out.WriteLine($"$ polypiler translate snippet.mjs --from javascript --to javascript --output out (exit code {refusal.ExitCode})");
    TestContext.Out.WriteLine(refusal.Output);
    TestContext.Out.WriteLine(File.ReadAllText(Path.Combine(this._directory, "out", "fnv1a64.mjs")));
    Assert.That((success.ExitCode, refusal.ExitCode), Is.EqualTo((0, 3)));
    Assert.That(success.Output, Does.Contain("fnv1a64.mjs: type hash: local h: u64 from 'BigInt.asUintN(64, (h ^ BigInt(data[i])) * 0x100000001B3n)' at (7,9)"));
  }

  /// <summary>What a call does in node on the original, in the interpreter and in node on the module the executable writes.</summary>
  private IReadOnlyList<string> Everywhere(string source, PrototypeCall call) {
    var (exitCode, output) = this.Translate(source);
    Assert.That(exitCode, Is.Zero, output);
    return [
      Toolchain.NodeOutcomes(this._directory, "snippet.mjs", [call], true).Single(),
      PrototypeFixture.InterpretedOutcome(JavaScriptFixture.Import("snippet.mjs", source), call),
      Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), "snippet.mjs", [call], true).Single()
    ];
  }

  private (int ExitCode, string Output) Translate(string source) {
    File.WriteAllText(Path.Combine(this._directory, "snippet.mjs"), source);
    return Toolchain.RunPolypiler(this._directory, "translate", "snippet.mjs", "--from", "javascript", "--to", "javascript", "--output", "out");
  }

}
