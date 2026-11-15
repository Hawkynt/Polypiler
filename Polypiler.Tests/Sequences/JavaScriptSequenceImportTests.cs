using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Sequences;

/// <summary>
/// Generators of JavaScript imported (#1327): the fixture <c>iterators.js</c> through the frontend and
/// the interpreter, both backends writing it in every profile, and every refused construct named.
/// What node computes of the original is compared in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class JavaScriptSequenceImportTests {

  private static IReadOnlyList<string> Refused(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Null);
    return [.. result.Diagnostics.Select(diagnostic => diagnostic.ToString())];
  }

  /// <summary>The values node computes of the fixture (node 22), which the interpreter computes as well.</summary>
  [TestCase("earlyBreak", 57189), TestCase("full", 718283896), TestCase("twice", 3), TestCase("returned", 71292), TestCase("throwInside", 7194)]
  [TestCase("nestedClose", 189), TestCase("nestedFull", 128339), TestCase("fromArray", 4567), TestCase("truths", 121), TestCase("objects", 34), TestCase("delegated", 567)]
  public void Interpreter_ComputesWhatNodeComputes(string function, double expected)
    => Assert.That(IrInterpreter.Call(SequenceFixtures.ImportedScript(), function, []), Is.EqualTo(expected));

  [TestCase(0, 0), TestCase(1, 0), TestCase(10, 88), TestCase(47, 512559679)]
  public void FibSum_WrapsAround(int n, double expected) => Assert.That(IrInterpreter.Call(SequenceFixtures.ImportedScript(), "fibSum", [(double)n]), Is.EqualTo(expected));

  [Test]
  public void Import_MakesSinglePassIteratorFunctions() {
    var module = SequenceFixtures.ImportedScript();
    var traced = module.Functions.Single(function => function.Name == "traced");
    Assert.That(traced.ReturnType, Is.EqualTo(IrType.Sequence(IrType.I32, false)));
    Assert.That(IrSequences.Statements(module.Functions.Single(function => function.Name == "outer").Body).OfType<IrYieldFrom>().Count(), Is.EqualTo(1));
    Assert.That(module.Functions.Single(function => function.Name == "flags").ReturnType, Is.EqualTo(IrType.Sequence(IrType.Bool, false)));
  }

  /// <summary>Test design 1 and 12: every C# profile writes the fixture, and it compiles there.</summary>
  [Test, Category("Slow")]
  public void EveryCSharpProfile_WritesTheFixture() {
    var module = SequenceFixtures.ImportedScript();
    foreach (var profile in CSharpProfile.All) {
      Assert.That(LegalizedCSharp.Check(module, profile), Is.Empty, profile.Name);
      var text = LegalizedCSharp.Emit(module, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile.Name}\n{text}");
    }
  }

  /// <summary>Test design 1 and 12: every JavaScript level writes the fixture, at that level.</summary>
  [Test]
  public void EveryJavaScriptLevel_WritesTheFixture() {
    var module = SequenceFixtures.ImportedScript();
    foreach (var profile in JavaScriptProfile.LevelNames.Select(level => JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm")).Append(JavaScriptProfile.Parse("es5", "cjs", typedArrays: true))) {
      Assert.That(LegalizedJavaScript.Check(module, profile), Is.Empty, profile.ToString());
      var text = LegalizedJavaScript.Emit(module, profile).Single().Content;
      Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, $"{profile}\n{text}");
    }
  }

  /// <summary>Test design 9: a yield that receives a value is refused, naming it.</summary>
  [Test]
  public void YieldAsAValue_IsRefused()
    => Assert.That(Refused("export function f() { function* g() { const x = yield 1; } for (const v of g()) {} return 0; }"),
      Has.Some.Contains("PLY1102").And.Some.Contains("yield as a value"));

  [TestCase("function* g() { yield; } export function f() { for (const v of g()) {} return 0; }", "yield without a value")]
  [TestCase("function* g() { yield 1; return 2; } export function f() { for (const v of g()) {} return 0; }", "a generator that returns a value")]
  [TestCase("export function f() { function* g() { yield 1; } let s = 0; for (const v of g()) { s += v; } return s; }", "a nested generator")]
  [TestCase("export async function* g() { yield 1; }", "an async generator")]
  [TestCase("function* g() { yield* 'ab'; } export function f() { for (const v of g()) {} return 0; }", "yield* of string")]
  public void Refusals_NameTheConstruct(string source, string named)
    => Assert.That(Refused(source), Has.Some.Contains("PLY1102").And.Some.Contains(named));

  /// <summary>Test design 11: a variable that holds a generator object and a number.</summary>
  [Test]
  public void GeneratorAndNumber_AreMixedKinds()
    => Assert.That(Refused("function* g() { yield 1; } export function f() { let s = g(); s = 2; return 0; }"), Has.Some.Contains("PLY1105"));

  [Test]
  public void YieldsOfTwoKinds_AreMixedKinds()
    => Assert.That(Refused("function* g() { yield 1; yield 'a'; } export function f() { for (const v of g()) {} return 0; }"), Has.Some.Contains("PLY1105"));

  /// <summary>Test design 9: next() on a generator object outside a for...of is refused.</summary>
  [Test]
  public void ManualNext_IsRefused()
    => Assert.That(Refused("function* g() { yield 1; } export function f() { const s = g(); s.next(); return 0; }"), Has.Some.Contains("PLY1102").And.Some.Contains("next"));

}
