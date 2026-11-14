using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
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

  /// <summary>
  /// The scenario "Mixed uint and int widens to long as in C#" checks the result; the header of the
  /// module also names the JavaScript types of the signature: a 64-bit result is a <c>bigint</c>.
  /// </summary>
  [Test]
  public void Header_NamesTheJavaScriptTypesOfTheSignature() {
    File.WriteAllText(Path.Combine(this._directory, "Snippet.cs"), PrototypeFixture.Wrap("public static long F(uint a, int b) => a + b;"));
    var (exitCode, text) = Toolchain.Translate(this._directory, "translate", "Snippet.cs", "--from", "csharp", "--to", "javascript", "--output", "out");
    Assert.That(exitCode, Is.Zero, text);
    Assert.That(File.ReadAllText(Path.Combine(this._directory, "out", "Snippet.mjs")), Does.Contain("// F(a: number, b: number): bigint"));
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
    this._inProcess = true;
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

  /// <summary>Translates a fixture with the real executable, or in process where the test is about the generated file, and runs the calls through node.</summary>
  private IReadOnlyList<string> TranslateFixture(string fixture, IReadOnlyList<PrototypeCall> calls) {
    var output = Path.Combine(this._directory, "out");
    var watch = Stopwatch.StartNew();
    var (exitCode, text) = Toolchain.Polypiler(this._inProcess, this._directory, "translate", PrototypeFixture.PathOf(fixture), "--from", "csharp", "--to", "javascript", "--output", output);
    TestContext.Out.WriteLine($"{fixture}: translated in {watch.ElapsedMilliseconds} ms");
    Assert.That(exitCode, Is.Zero, text);
    return Toolchain.NodeOutcomes(output, PrototypeFixture.ClassOf(fixture) + ".mjs", calls, true);
  }

  private (int ExitCode, string Output) TranslateText(string source) {
    File.WriteAllText(Path.Combine(this._directory, "Snippet.cs"), source);
    return Toolchain.RunPolypiler(this._directory, "translate", "Snippet.cs", "--from", "csharp", "--to", "javascript", "--output", "out");
  }

}
