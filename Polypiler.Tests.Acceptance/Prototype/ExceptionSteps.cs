using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/ir/ir-exceptions.feature</c>. The expected value is never computed
/// here: it is what .NET computes of the original C# source, or what <c>node</c> computes of the
/// original JavaScript, or the literal value of the scenario, which those give.
/// </summary>
[Binding, Scope(Feature = "Exceptions and traps")]
public sealed class ExceptionSteps(PrototypeScenario scenario) {

  private (string Path, string Text) _generated;
  private ProfileCase? _profile;
  private CSharpProfile? _csharp;

  /// <summary>The profile of a level in the module format the level starts with: an ES module, or CommonJS at ECMAScript 5.</summary>
  private static ProfileCase Level(string level) => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

  /// <summary>Translates the source with the options of the step, which name the target and its profile.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var to = arguments[arguments.IndexOf("--to") + 1];
    var name = arguments[arguments.IndexOf("--profile") + 1];
    var from = Path.GetExtension(scenario.Source) == ".cs" ? "csharp" : "javascript";
    if (to == "csharp") {
      this._csharp = CSharpProfile.Parse(name);
      this._generated = scenario.Translate(scenario.Source, from, to, "--profile", this._csharp.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(this._generated.Text, this._csharp.Name, this._csharp.LanguageVersionName), Is.Empty, this._generated.Text);
      return;
    }

    this._profile = Level(name);
    this._generated = scenario.Translate(scenario.Source, from, "javascript", this._profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(this._generated.Text, this._profile.Parsed), Is.Empty, this._generated.Text);
  }

  /// <summary>The error a trap throws in node, as the scenario describes it: its chain, name, message and text.</summary>
  [Then(@"^Div\((-?\d+), (-?\d+)\) throws an error e with e instanceof RangeError (true|false), e\.name " + StepText.Quoted + ", e\\.message " + StepText.Quoted + " and String\\(e\\) " + StepText.Quoted + "$")]
  public void DivThrows(int a, int b, string range, string name, string message, string text) {
    Assert.That(() => _ = a / b, Throws.TypeOf<DivideByZeroException>(), ".NET");
    var shown = ClassesSteps.Node(this._generated.Path, this._profile!.Module == "esm", "Div",
      $"try {{ Div({a}, {b}); console.log('none'); }} catch (e) {{ console.log(JSON.stringify([e instanceof RangeError, e.name, e.message, String(e)])); }}");
    Assert.That(shown, Is.EqualTo($"[{range},\"{name}\",\"{message}\",\"{text}\"]"), this._generated.Text);
  }

}
