using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Exceptions;

/// <summary>
/// The exception fixtures (#1323) through the C# frontend, the interpreter and both backends: the
/// interpreter computes what .NET computes of the original, and every profile of both backends writes
/// every fixture at its level. That the generated code computes the same is decided by the C#
/// compiler and node in the acceptance tests.
/// </summary>
[TestFixture]
internal sealed class ExceptionFixtureTests {

  private static readonly JavaScriptProfile[] _levels = [.. JavaScriptProfile.LevelNames.Select(level => JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm"))];

  private static IEnumerable<TestCaseData> Calls()
    => ExceptionFixtures.Calls().Select(entry => new TestCaseData(entry.File, entry.Class, entry.Call).SetName($"{entry.Class}.{entry.Call}"));

  /// <summary>
  /// Test design 1, 2, 3 and 6: the interpreter computes what .NET computes, call by call. The compiled
  /// fixtures keep their trace in static fields shared by every call, so the cases run one at a time.
  /// </summary>
  [TestCaseSource(nameof(Calls)), NonParallelizable]
  public void Interpreter_ComputesWhatDotNetComputes(string file, string className, PrototypeCall call)
    => Assert.That(PrototypeFixture.InterpretedOutcome(ExceptionFixtures.Imported(file), call),
      Is.EqualTo(PrototypeFixture.InvokedOutcome(ExceptionFixtures.Compiled(className, call.Method), call)));

  /// <summary>The message of an exception made without one names its class; .NET names it with its namespace, a class of the IR has none.</summary>
  [Test]
  public void DefaultMessage_NamesTheClass() {
    var call = new PrototypeCall("DefaultMessage", []);
    Assert.That(PrototypeFixture.InterpretedOutcome(ExceptionFixtures.Imported("Classes"), call), Is.EqualTo("Exception of type 'X' was thrown."));
    Assert.That(PrototypeFixture.InvokedOutcome(ExceptionFixtures.Compiled("ClassesDemo", "DefaultMessage"), call),
      Is.EqualTo($"Exception of type '{ExceptionFixtures.Namespace}.X' was thrown."));
  }

  /// <summary>Test design 5: the filter with an effect runs before the inner finally block, as .NET runs it.</summary>
  [Test]
  public void FilterWithAnEffect_RunsBeforeTheFinallyBlock() {
    var call = new PrototypeCall("Run", []);
    Assert.That(PrototypeFixture.InvokedOutcome(ExceptionFixtures.Compiled("OrderDemo", "Run"), call), Is.EqualTo("W,F,C"));
    Assert.That(PrototypeFixture.InterpretedOutcome(ExceptionFixtures.Imported("order"), call), Is.EqualTo("W,F,C"));
  }

  /// <summary>Test design 1 and 12: every fixture is written in every C# profile, and compiles there.</summary>
  [TestCase("Traps"), TestCase("Classes")]
  [Category("Slow")]
  public void EveryCSharpProfile_WritesTheFixture(string file) {
    var module = ExceptionFixtures.Imported(file);
    foreach (var profile in CSharpProfile.All) {
      Assert.That(LegalizedCSharp.Check(module, profile), Is.Empty, profile.Name);
      var text = LegalizedCSharp.Emit(module, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile.Name}\n{text}");
    }
  }

  /// <summary>Test design 1 and 12: every fixture is written at every JavaScript level, at that level.</summary>
  [TestCase("Traps"), TestCase("Classes")]
  public void EveryJavaScriptLevel_WritesTheFixture(string file) {
    var module = ExceptionFixtures.Imported(file);
    foreach (var profile in _levels) {
      Assert.That(LegalizedJavaScript.Check(module, profile), Is.Empty, profile.ToString());
      var text = LegalizedJavaScript.Emit(module, profile).Single().Content;
      Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, $"{profile}\n{text}");
    }
  }

  /// <summary>Test design 5: the filter with an effect over a finally block is native from C# 6 and refused below and in JavaScript.</summary>
  [Test]
  public void FilterWithAnEffect_IsNativeFromCSharp6AndRefusedElsewhere() {
    var module = ExceptionFixtures.Imported("order");
    foreach (var profile in CSharpProfile.All) {
      var problems = LegalizedCSharp.Check(module, profile).Select(problem => problem.ToString()).ToList();
      if (profile.LanguageVersion >= new System.Version(6, 0))
        Assert.That(problems, Is.Empty, profile.Name);
      else
        Assert.That(problems, Has.One.Contains(CSharpEmitter.Inexpressible).And.Contains("runs before finally blocks").And.Contains(profile.Name));
    }

    foreach (var profile in _levels)
      Assert.That(LegalizedJavaScript.Check(module, profile).Select(problem => problem.ToString()), Has.One.Contains(JavaScriptEmitter.Inexpressible).And.Contains("runs before finally blocks"));
  }

}
