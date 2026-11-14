using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
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

  private static readonly bool[] _overflowChecking = [false, true];

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

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

}
