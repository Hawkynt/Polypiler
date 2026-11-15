using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Functions;

/// <summary>
/// Function values written by the JavaScript backend in every profile (#1326) - arrow functions from
/// ES2015, function expressions in ES5, the let of every pass in the header of a for, the environment
/// object of a pass in ES5 - and the JavaScript fixtures written as C# for every profile. Running the
/// output in node is the acceptance tests' part; here every output parses at its level, and the C# one
/// runs.
/// </summary>
[TestFixture]
internal sealed class JavaScriptFunctionsEmissionTests {

  private static readonly JavaScriptProfile[] _profiles = [
    .. from level in JavaScriptProfile.LevelNames
       from module in JavaScriptProfile.ModuleNames
       where level != "es5" || module != "esm"
       from typed in level == "es5" ? new[] { false, true } : [false]
       select JavaScriptProfile.Parse(level, module, module == "iife" ? "G" : null, typed)
  ];

  private static IEnumerable<IrModule> Modules()
    => FunctionsFixtures.CSharp.Select(fixture => FunctionsFixtures.Imported(fixture.File)).Concat(FunctionsFixtures.JavaScript.Select(FunctionsFixtures.ImportedScript));

  private static string Emit(IrModule module, JavaScriptProfile profile) {
    Assert.That(JavaScriptEmitter.Check(module, profile), Is.Empty);
    var text = JavaScriptEmitter.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, text);
    return text;
  }

  private static string Emit(IrModule module, string level) => Emit(module, JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm"));

  /// <summary>Test design 1, 6 and 13: every fixture is written in every profile, nothing inexpressible, and the output parses at its level.</summary>
  [Test]
  public void Fixtures_AreWrittenInEveryProfile() {
    foreach (var module in Modules())
      foreach (var profile in _profiles)
        Emit(module, profile);
  }

  /// <summary>Scenario "Each profile writes the form it has": ES5 has no arrow, no let and no const.</summary>
  [Test]
  public void Es5_WritesFunctionExpressionsAndVar() {
    var text = Emit(FunctionsFixtures.Imported("Closures"), "es5");
    Assert.That(text, Does.Not.Contain("=>").And.Not.Contain("let ").And.Not.Contain("const "));
    Assert.That(text, Does.Contain("var F = function F(m) {"), "a local function");
    Assert.That(text, Does.Contain("$c1 = {};").And.Contain("(function ($c1) {"), "the foreach element lives in the environment of its pass");
    Assert.That(text, Does.Contain("var $self").Or.Not.Contain("$self"));
  }

  /// <summary>A C# for shares its variable: ES2015 declares it before the loop, never in a for-let header.</summary>
  [TestCase("es2015")]
  [TestCase("es2022")]
  public void CSharpFor_KeepsItsLetInFrontOfTheLoop(string level) {
    var text = Emit(FunctionsFixtures.Imported("Closures"), level);
    var forCapture = text[text.IndexOf("function ForCapture(", System.StringComparison.Ordinal)..];
    forCapture = forCapture[..forCapture.IndexOf("\n}\n", System.StringComparison.Ordinal)];
    Assert.That(forCapture, Does.Not.Contain("for (let").And.Contain("let i = 0;"));
    Assert.That(text, Does.Contain("const F = (m) => "));
    Assert.That(text, Does.Contain("Compose((v) => (v + 1) | 0, (v) => Math.imul(v, 3))(x)"));
  }

  /// <summary>A for-let of JavaScript is a for-let again from ES2015, and an environment object of its passes in ES5, copied back where the body writes the variable.</summary>
  [Test]
  public void ForLet_IsAForLetOrAnEnvironment() {
    var module = FunctionsFixtures.ImportedScript("closures.js");
    var es2015 = Emit(module, "es2015");
    Assert.That(es2015, Does.Contain("for (let i = 0; i < 3; i = i + 1)").And.Contain("for (let i = 0; i < 6; i = i + 1)"));
    Assert.That(es2015, Does.Contain("for (var i").Or.Contain("let i = 0;\n"));
    var es5 = Emit(module, "es5");
    Assert.That(es5, Does.Contain("$c1 = { i: i };").And.Contain("finally {").And.Contain("i = $c1.i;"));
    Assert.That(es5, Does.Not.Contain("=>"));
  }

  /// <summary>A bound method is a closure over its receiver evaluated once, never bind.</summary>
  [TestCase("es5", "(function (o) {")]
  [TestCase("es2022", "((o) => (a1) => o.Apply(a1))(scale)")]
  public void BoundMethod_ClosesOverItsReceiver(string level, string written) {
    var text = Emit(FunctionsFixtures.Imported("Closures"), level);
    Assert.That(text, Does.Contain(written).And.Not.Contain(".bind("));
  }

  /// <summary>Scenario "A JavaScript let makes one variable per pass": the C# translation of the JavaScript fixture, for every profile, computes what node does.</summary>
  [TestCaseSource(typeof(CSharpProfile), nameof(CSharpProfile.All))]
  public void JavaScriptFixture_AsCSharp_ComputesWhatNodeComputes(CSharpProfile profile) {
    var text = CSharpEmitter.Emit(FunctionsFixtures.ImportedScript("closures.js"), profile)[0].Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var build = GeneratedCSharp.Compile(text, false);
    string Run(string method, params object[] arguments) => build.Outcome(new PrototypeCall(method, arguments));
    Assert.That((Run("forVar"), Run("forLet"), Run("forLetAssigned")), Is.EqualTo(("9", "3", "9")), text);
    Assert.That((Run("counter"), Run("interleaved"), Run("late", 2.0), Run("fact", 5.0), Run("fact", 0.0), Run("composed", 4.0)),
      Is.EqualTo((PrototypeFixture.Show(123.0), PrototypeFixture.Show(21.0), PrototypeFixture.Show(10.0), PrototypeFixture.Show(120.0), PrototypeFixture.Show(1.0), PrototypeFixture.Show(15.0))));
    Assert.That(Run("callNull"), Is.EqualTo(PrototypeFixture.Trap(IrTrap.NullReference)));
  }

  /// <summary>The sort and the fold of JavaScript, written as C# for every profile, compute what the C# fixtures compute.</summary>
  [TestCaseSource(typeof(CSharpProfile), nameof(CSharpProfile.All))]
  public void SortAndFold_AsCSharp_ComputeWhatDotNetComputes(CSharpProfile profile) {
    foreach (var (script, file) in new[] { ("sort.js", "Sort"), ("accumulate.js", "Accumulate") }) {
      var text = CSharpEmitter.Emit(FunctionsFixtures.ImportedScript(script), profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      var build = GeneratedCSharp.Compile(text, false);
      foreach (var (_, className, call) in FunctionsFixtures.Calls().Where(call => call.File == file && call.Call.Method != "Calls")) {
        var named = call with { Method = char.ToLowerInvariant(call.Method[0]) + call.Method[1..], Arguments = [.. call.Arguments.Select(argument => argument is int whole ? (object)(double)whole : argument)] };
        Assert.That(build.Outcome(named), Is.EqualTo(PrototypeFixture.InvokedOutcome(FunctionsFixtures.Compiled(className, call.Method), call)), $"{call}\n{text}");
      }
    }
  }

}
