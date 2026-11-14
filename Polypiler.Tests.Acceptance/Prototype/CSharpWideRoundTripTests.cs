using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
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
[Layer("core")]
public sealed class CSharpWideRoundTripTests {

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

  /// <summary>
  /// Test design 1 to 6, 8 to 11 and 13: every call of every wide fixture — the published vectors,
  /// each operator and each conversion on each type at its boundaries and on a thousand random
  /// operand tuples (seed 2026), each statement kind, each library operation and each trap — has one
  /// outcome in the file the command wrote - through its own entry point, in process - compiled
  /// without and with overflow checking, in the original fixture and in the interpreter.
  /// </summary>
  [TestCaseSource(nameof(WideFixtures))]
  public void CompiledOutput_Original_AndInterpreter_Agree(string fixture) {
    var calls = PrototypeCases.For(fixture);
    var module = PrototypeFixture.Imported(fixture);
    var output = Path.Combine(this._directory, "out");
    var watch = Stopwatch.StartNew();
    var (exitCode, text) = Toolchain.Translate(this._directory, "translate", PrototypeFixture.PathOf(fixture), "--from", "csharp", "--to", "csharp", "--output", output);
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

}
