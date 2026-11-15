using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Recognize;

/// <summary>
/// The recognition fixtures: every rule fires exactly where its fixture expects it and nowhere else
/// (test design 1 and 8), the optimized IR computes what the unoptimized IR computes - result, trap
/// and the arrays a trap leaves behind - exhaustively on 8 and 16 bits and on the edges and 10 000
/// random tuples elsewhere (2), a disabled rule never fires while the others still do (4), and the
/// result is the same on every run (12).
/// </summary>
[TestFixture]
public sealed class RecognitionFixtureTests {

  private static IEnumerable<TestCaseData> Files() => RecognitionFixtures.Files.Select(file => new TestCaseData(file).SetArgDisplayNames(file));

  /// <summary>The most calls one case of the equivalence test makes; a function with more is split into parts that run side by side.</summary>
  private const long CallsPerCase = 1 << 19;

  private static IEnumerable<TestCaseData> Functions() => RecognitionFixtures.Files.SelectMany(file
    => RecognitionFixtures.Imported(file).Functions.Where(function => function.IsPublic)
      .SelectMany(function => Parts(file, function)));

  /// <summary>Each fixture file imported and optimized once for all the cases of its functions, during one run.</summary>
  private static readonly ConcurrentDictionary<string, Lazy<(IrModule Original, IrModule Optimized)>> _optimized = new(StringComparer.Ordinal);

  private static IEnumerable<TestCaseData> Parts(string file, IrFunction function) {
    var parts = (int)((RecognitionFixtures.CallCount(function) + CallsPerCase - 1) / CallsPerCase);
    return parts == 1
      ? [new TestCaseData(file, function.Name, 0, 1).SetArgDisplayNames(file, function.Name)]
      : Enumerable.Range(0, parts).Select(part => new TestCaseData(file, function.Name, part, parts).SetArgDisplayNames(file, function.Name, $"part {part + 1} of {parts}"));
  }

  private static Dictionary<string, string[]> Applied(OptimizationResult result)
    => result.Applications.GroupBy(application => application.Function)
      .ToDictionary(group => group.Key, group => group.Select(application => application.Id).Order(StringComparer.Ordinal).ToArray());

  [TestCaseSource(nameof(Files))]
  public void EveryFunction_HasTheExpectedApplications(string file) {
    var module = RecognitionFixtures.Imported(file);
    var expected = RecognitionFixtures.Expectations(file);
    var applied = Applied(Optimizer.Run(module, RecognitionFixtures.Options));
    var functions = module.Functions.Where(function => function.IsPublic).Select(function => function.Name).ToList();
    Assert.That(expected.Keys, Is.EquivalentTo(functions), "every public function says what it expects");
    var differences = functions.Where(name => !expected[name].SequenceEqual(applied.GetValueOrDefault(name, [])))
      .Select(name => $"{name}: expected [{string.Join(" ", expected[name])}], applied [{string.Join(" ", applied.GetValueOrDefault(name, []))}]").ToList();
    Assert.That(differences, Is.Empty);
  }

  /// <summary>
  /// Test design 2. Up to 2^19 calls per case through the same interpreter paths: under the coverage
  /// collector it took more than half of the gate's time and adds no line the first calls do not
  /// reach, so it is <c>Exhaustive</c>, which the layer jobs run on every OS and the coverage gate
  /// leaves out.
  /// </summary>
  [TestCaseSource(nameof(Functions)), Category("Slow"), Category("Exhaustive")]
  public void OptimizedFunction_ComputesWhatTheOriginalComputes(string file, string function, int part, int parts) {
    var (original, optimized) = _optimized.GetOrAdd(file, key => new(() => {
      var imported = RecognitionFixtures.Imported(key);
      return (imported, Optimizer.Run(imported, RecognitionFixtures.Options).Module);
    })).Value;
    var cases = 0;
    var mismatches = new List<string>();
    foreach (var call in RecognitionFixtures.Calls(original.Functions.Single(candidate => candidate.Name == function), part, parts)) {
      ++cases;
      var expected = RecognitionFixtures.Outcome(original, call);
      var actual = RecognitionFixtures.Outcome(optimized, call);
      if (expected != actual && mismatches.Count < 10)
        mismatches.Add($"{call}: {actual}, not {expected}");
    }

    TestContext.Out.WriteLine($"{file} {function}, part {part + 1} of {parts}: {cases} cases, {mismatches.Count} mismatches");
    Assert.That(mismatches, Is.Empty);
  }

  [TestCaseSource(nameof(Files))]
  public void DisabledRule_NeverFires_WhileTheOthersStillDo(string file) {
    var module = RecognitionFixtures.Imported(file);
    var expected = RecognitionFixtures.Expectations(file);
    foreach (var id in expected.Values.SelectMany(ids => ids).Distinct()) {
      var applied = Applied(Optimizer.Run(module, RecognitionFixtures.Options with { Disabled = new HashSet<string> { id } }));
      Assert.That(applied.Values.SelectMany(ids => ids), Does.Not.Contain(id), id);
      foreach (var (function, ids) in expected) {
        var others = ids.Where(other => other != id).ToList();
        Assert.That(applied.GetValueOrDefault(function, []).Where(other => others.Contains(other)).Count(), Is.EqualTo(others.Count), $"{id} off, {function}");
      }
    }
  }

  [TestCaseSource(nameof(Files))]
  public void Optimization_IsTheSameOnEveryRun(string file) {
    var first = Optimizer.Run(RecognitionFixtures.Imported(file), RecognitionFixtures.Options);
    var second = Optimizer.Run(RecognitionFixtures.Imported(file), RecognitionFixtures.Options);
    Assert.That(IrText.Of(second.Module), Is.EqualTo(IrText.Of(first.Module)));
    Assert.That(second.Applications, Is.EqualTo(first.Applications));
  }

  [TestCaseSource(nameof(Files))]
  public void OptimizedModule_IsWellTyped(string file)
    => Assert.That(IrVerifier.Verify(Optimizer.Run(RecognitionFixtures.Imported(file), RecognitionFixtures.Options).Module), Is.Empty);

  private static IEnumerable<TestCaseData> CSharpFiles() => RecognitionFixtures.CSharp.Select(name => new TestCaseData(name).SetArgDisplayNames(name));

  /// <summary>
  /// The functions whose build is no oracle. .NET's JIT turns <c>(x &lt;&lt; n) ^ (x &gt;&gt; (32 - n))</c>
  /// into a rotation, also at <c>n = 0</c>, where C# masks both counts to 0 and defines the result as
  /// <c>x ^ x = 0</c> (ECMA-334, shift operators) while the rotation gives <c>x</c>
  /// (docs/reference/known-runtime-defects.md). The interpreter follows the language there, and O0100
  /// rightly leaves the expression alone. Their compiled oracle is the build of the C# the backend
  /// writes for the original IR, which the JIT computes as C# defines it (#1310).
  /// </summary>
  private static readonly HashSet<string> _jitDiffers = ["RecognizeRotate.XorVariable"];

  /// <summary>
  /// Oracle 2: the optimized IR computes what a compiled build of the fixture computes, on the edges
  /// of every function - the C# compiler's build of the fixture, or for <see cref="_jitDiffers"/> that
  /// of the generated C#.
  /// </summary>
  [TestCaseSource(nameof(CSharpFiles)), Category("Slow")]
  public void OptimizedFixture_ComputesWhatItsBuildComputes(string fixture) {
    var original = RecognitionFixtures.Imported(fixture + ".cs");
    var optimized = Optimizer.Run(original, RecognitionFixtures.Options).Module;
    var written = new Lazy<GeneratedCSharp>(() => GeneratedCSharp.Compile(CSharpEmitter.Emit(original).Single().Content, false));
    var mismatches = new List<string>();
    foreach (var function in optimized.Functions.Where(function => function.IsPublic)) {
      var differs = _jitDiffers.Contains($"{fixture}.{function.Name}");
      foreach (var call in RecognitionFixtures.Calls(function).Take(3000))
        if (PrototypeFixture.InterpretedOutcome(optimized, call) is var actual
          && (differs ? written.Value.Outcome(call) : PrototypeFixture.CompiledOutcome(fixture, call)) is var expected && actual != expected)
          mismatches.Add($"{call}: {actual}, not {expected}");
    }

    Assert.That(mismatches.Take(10), Is.Empty);
  }

  /// <summary>
  /// The reason for <see cref="_jitDiffers"/>: the C# compiler's build of each of them differs from the
  /// generated C# only where a count is 0 modulo 32, and the generated C# gives what C# defines there.
  /// </summary>
  [Test, Category("Slow")]
  public void JitDiffers_DifferOnlyAtCountZero() {
    var original = RecognitionFixtures.Imported("RecognizeRotate.cs");
    var written = GeneratedCSharp.Compile(CSharpEmitter.Emit(original).Single().Content, false);
    var call = new PrototypeCall("XorVariable", [0x12345678u, 0]);
    Assert.That(written.Outcome(call), Is.EqualTo("0").And.EqualTo(PrototypeFixture.InterpretedOutcome(original, call)));
    var differing = RecognitionFixtures.Calls(original.Functions.Single(function => function.Name == "XorVariable")).Take(3000)
      .Where(candidate => written.Outcome(candidate) != PrototypeFixture.CompiledOutcome("RecognizeRotate", candidate));
    Assert.That(differing.Select(candidate => (int)candidate.Arguments[1] % 32), Has.All.Zero);
  }

  /// <summary>Oracle 3: every optimized fixture is written by every profile of both backends - C# compiled for its framework, JavaScript parsed at its level.</summary>
  [TestCaseSource(nameof(Files)), Category("Slow")]
  public void OptimizedFixture_IsWrittenByEveryProfile(string file) {
    var optimized = Optimizer.Run(RecognitionFixtures.Imported(file), RecognitionFixtures.Options).Module;
    foreach (var level in new[] { "es5", "es2015", "es2017", "es2020", "es2022" }) {
      var profile = JavaScriptProfile.Parse(level);
      Assert.That(JavaScriptEmitter.Check(optimized, profile), Is.Empty, level);
      Assert.That(GeneratedJavaScript.Problems(JavaScriptEmitter.Emit(optimized, profile).Single().Content, profile), Is.Empty, level);
    }

    foreach (var profile in CSharpProfile.All) {
      var text = CSharpEmitter.Emit(optimized, profile).Single().Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
    }
  }

}
