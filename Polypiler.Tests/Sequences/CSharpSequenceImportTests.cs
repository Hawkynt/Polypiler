using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Sequences;

/// <summary>
/// Iterator methods of C# imported (#1327): the fixture <c>Iterators.cs</c> through the frontend and the
/// interpreter computes what .NET computes of it, both backends write it in every profile, and every
/// refused construct is named.
/// </summary>
[TestFixture]
internal sealed class CSharpSequenceImportTests {

  private static IEnumerable<TestCaseData> Calls()
    => SequenceFixtures.Calls().Select(call => new TestCaseData(call).SetName($"Iterators.{call}"));

  private static IReadOnlyList<string> Refused(string methods) {
    var result = CSharpFrontend.Import("Snippet.cs", "using System.Collections.Generic;\n" + PrototypeFixture.Wrap(methods));
    Assert.That(result.Module, Is.Null);
    return [.. result.Diagnostics.Select(diagnostic => diagnostic.ToString())];
  }

  /// <summary>Test design 1 to 7: the interpreter computes what .NET computes, call by call.</summary>
  [TestCaseSource(nameof(Calls))]
  public void Interpreter_ComputesWhatDotNetComputes(PrototypeCall call)
    => Assert.That(PrototypeFixture.InterpretedOutcome(SequenceFixtures.Imported(), call), Is.EqualTo(PrototypeFixture.InvokedOutcome(SequenceFixtures.Compiled(call.Method), call)));

  /// <summary>The scenario values of the feature, as .NET measures them.</summary>
  [TestCase("EarlyBreak", 57189), TestCase("Full", 718283896), TestCase("Twice", 6), TestCase("Throws", 14)]
  public void Fixture_HasTheScenarioValues(string method, int expected)
    => Assert.That(SequenceFixtures.Compiled(method).Invoke(null, []), Is.EqualTo(expected));

  [TestCase(10, 88), TestCase(0, 0), TestCase(47, 512559679)]
  public void FibSum_WrapsAround(int n, int expected) => Assert.That(IrInterpreter.Call(SequenceFixtures.Imported(), "FibSum", [n]), Is.EqualTo(expected));

  [TestCase(-1, 0), TestCase(0, 1)]
  public void Guard_EndsEarly(int n, int expected) => Assert.That(IrInterpreter.Call(SequenceFixtures.Imported(), "Guard", [n]), Is.EqualTo(expected));

  [Test]
  public void Nested_Delegates() => Assert.That(IrInterpreter.Call(SequenceFixtures.Imported(), "Nested", [3]), Is.EqualTo(34));

  [Test]
  public void Import_MakesIteratorFunctions() {
    var module = SequenceFixtures.Imported();
    var traced = module.Functions.Single(function => function.Name == "Traced");
    Assert.That(traced.ReturnType, Is.EqualTo(IrType.Sequence(IrType.I32, true)));
    Assert.That(IrSequences.IsIterator(traced), Is.True);
    var doubled = module.Functions.Single(function => function.Name == "Doubled");
    Assert.That(IrSequences.Statements(doubled.Body).OfType<IrYieldFrom>().Count(), Is.EqualTo(1));
    Assert.That(IrSequences.Statements(module.Functions.Single(function => function.Name == "Gate").Body).OfType<IrYieldBreak>().Count(), Is.EqualTo(1));
  }

  /// <summary>Test design 1 and 12: every C# profile writes the fixture, and it compiles there.</summary>
  [Test, Category("Slow")]
  public void EveryCSharpProfile_WritesTheFixture() {
    var module = SequenceFixtures.Imported();
    foreach (var profile in CSharpProfile.All) {
      Assert.That(CSharpEmitter.Check(module, profile), Is.Empty, profile.Name);
      var text = CSharpEmitter.Emit(module, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile.Name}\n{text}");
    }
  }

  /// <summary>Test design 1 and 12: every JavaScript level writes the fixture, at that level.</summary>
  [Test]
  public void EveryJavaScriptLevel_WritesTheFixture() {
    var module = SequenceFixtures.Imported();
    foreach (var profile in JavaScriptProfile.LevelNames.Select(level => JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm")).Append(JavaScriptProfile.Parse("es5", "cjs", typedArrays: true))) {
      Assert.That(JavaScriptEmitter.Check(module, profile), Is.Empty, profile.ToString());
      var text = JavaScriptEmitter.Emit(module, profile).Single().Content;
      Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, $"{profile}\n{text}");
    }
  }

  /// <summary>Test design 9: manual enumeration is refused, naming it.</summary>
  [Test]
  public void ManualEnumeration_IsRefused()
    => Assert.That(Refused("public static int F(IEnumerable<int> s) { var e = s.GetEnumerator(); e.MoveNext(); return e.Current; }"),
      Has.Some.Contains("PLY1002").And.Some.Contains("GetEnumerator"));

  [Test]
  public void NonGenericEnumerable_IsRefused()
    => Assert.That(Refused("public static int F(System.Collections.IEnumerable s) { return 0; }"), Has.Some.Contains("System.Collections.IEnumerable"));

  [Test]
  public void IteratorLocalFunction_IsRefused()
    => Assert.That(Refused("public static int F() { IEnumerable<int> G() { yield return 1; } var s = 0; foreach (var v in G()) s += v; return s; }"),
      Has.Some.Contains("an iterator local function or lambda"));

  [Test]
  public void SequenceReturnedWithoutIterator_IsRefused()
    => Assert.That(Refused("private static IEnumerable<int> G() { yield return 1; } private static IEnumerable<int> H() { return G(); } public static int F() { return 0; }"),
      Has.Some.Contains("a method that returns an IEnumerable<T> without being an iterator"));

  [Test]
  public void ListAsSequence_IsRefused()
    => Assert.That(Refused("private static int S(IEnumerable<int> s) { var t = 0; foreach (var v in s) t += v; return t; } public static int F() { var l = new List<int>(); l.Add(1); return S(l); }"),
      Has.Some.Contains("as an IEnumerable<T>"));

}
