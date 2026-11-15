using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Collections;

/// <summary>
/// The collection fixtures (#1328) through the C# frontend, the interpreter and both backends: the
/// interpreter computes what .NET computes of the original, and every profile of both backends writes
/// every fixture at its level. That the generated code computes the same is decided by the C#
/// compiler and node in the acceptance tests.
/// </summary>
[TestFixture]
internal sealed class CollectionFixtureTests {

  private static readonly JavaScriptProfile[] _levels = [
    .. JavaScriptProfile.LevelNames.Select(level => JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm")),
    JavaScriptProfile.Parse("es5", "cjs", typedArrays: true)
  ];

  private static IEnumerable<TestCaseData> Calls()
    => CollectionFixtures.Calls().Select(entry => new TestCaseData(entry.File, entry.Class, entry.Call).SetName($"{entry.Class}.{entry.Call}"));

  /// <summary>Test design 1, 4, 5, 6, 7 and 10: the interpreter computes what .NET computes, call by call.</summary>
  [TestCaseSource(nameof(Calls))]
  public void Interpreter_ComputesWhatDotNetComputes(string file, string className, PrototypeCall call)
    => Assert.That(PrototypeFixture.InterpretedOutcome(CollectionFixtures.Imported(file), call),
      Is.EqualTo(PrototypeFixture.InvokedOutcome(CollectionFixtures.Compiled(className, call.Method), call)));

  /// <summary>Test design 1, 9 and 13: every fixture is written in every C# profile, and compiles there.</summary>
  [TestCase("Collections"), TestCase("WordFrequency")]
  [Category("Slow")]
  public void EveryCSharpProfile_WritesTheFixture(string file) {
    var module = CollectionFixtures.Imported(file);
    foreach (var profile in CSharpProfile.All) {
      Assert.That(LegalizedCSharp.Check(module, profile), Is.Empty, profile.Name);
      var text = LegalizedCSharp.Emit(module, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile.Name}\n{text}");
    }
  }

  /// <summary>Test design 1, 9 and 13: every fixture is written at every JavaScript level, at that level.</summary>
  [TestCase("Collections"), TestCase("WordFrequency")]
  public void EveryJavaScriptLevel_WritesTheFixture(string file) {
    var module = CollectionFixtures.Imported(file);
    foreach (var profile in _levels) {
      Assert.That(LegalizedJavaScript.Check(module, profile), Is.Empty, profile.ToString());
      var text = LegalizedJavaScript.Emit(module, profile).Single().Content;
      Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, $"{profile}\n{text}");
    }
  }

}
