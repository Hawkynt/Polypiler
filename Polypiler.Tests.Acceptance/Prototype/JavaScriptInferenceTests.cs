using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
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

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>The scenario "An unbounded BigInt is refused, a bounded one is a 64-bit integer" with the executable: the whole diagnostic, its position included.</summary>
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

  /// <summary>The scenario "A variable holding a number and an array is refused" with the executable: the whole diagnostic, its position included.</summary>
  [Test]
  public void VariableHoldingANumberAndAnArray_IsRefused() {
    var (exitCode, output) = this.Translate("export function f(n) { let x = 0; if (n > 1) x = new Uint8Array(n); return x; }");
    Assert.That(exitCode, Is.EqualTo(3));
    Assert.That(output, Does.Contain("snippet.mjs(1,50): error PLY1105: 'x' holds both number and uint8[]"));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
  }

  /// <summary>The scenario "A switch section that falls through into statements is refused" with the executable: the whole diagnostic.</summary>
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
    Assert.That(success.Output, Does.Contain("fnv1a64.mjs: type hash: local h: u64 from '0xCBF29CE484222325n' at (5,11)"));
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
