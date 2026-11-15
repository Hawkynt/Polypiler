using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/ir/ir-generics.feature</c>. The expected value is never computed
/// here: it is what .NET computes of the original C# fixture, built into this assembly, or what
/// <c>node</c> computes of the original JavaScript; the step's own numbers are checked against that oracle too.
/// </summary>
[Binding, Scope(Feature = "Generic methods and classes")]
public sealed class GenericsSteps(PrototypeScenario scenario) {

  private (string Path, string Text) _generated;
  private ProfileCase? _profile;
  private CSharpProfile? _csharp;

  /// <summary>The profile of a level in the module format the level starts with: an ES module, or CommonJS at ECMAScript 5.</summary>
  private static ProfileCase Level(string level) => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

  /// <summary>A fixture of the folder with the scenario's own description of it.</summary>
  [Given("^the fixture " + StepText.Quoted + " with " + StepText.Quoted + "$")]
  public void DescribedFixture(string path, string text) {
    Assert.That(File.ReadAllText(GenericsFixtures.PathOf(Path.GetFileName(path))), Does.Contain(StepText.Unescape(text)));
    scenario.Workspace.Copy(GenericsFixtures.PathOf(Path.GetFileName(path)), path);
    scenario.Source = path;
  }

  /// <summary>A JavaScript module of the scenario's own.</summary>
  [Given("^the JavaScript module " + StepText.Quoted + "$")]
  public void JavaScriptModule(string text) => scenario.Script("module.js", StepText.Unescape(text));

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

  /// <summary>Runs the translate command on the source with the options of the step.</summary>
  [When("^I run the translate command with " + StepText.Quoted + "$")]
  public void RunTranslate(string options)
    => scenario.Polypiler.Run(["translate", scenario.Source, "--from", Path.GetExtension(scenario.Source) == ".cs" ? "csharp" : "javascript", "--output", "out", .. CommandLine.Split(options)]);

  /// <summary>
  /// What the calls of a fixture class give in the generated file - compiled for C#, in node for
  /// JavaScript - which is what .NET gives of the fixture, and the values the scenario states.
  /// </summary>
  private void Agree(string className, IReadOnlyList<PrototypeCall> calls, IReadOnlyList<string>? stated = null) {
    var typed = calls.Select(call => call with { Returns = GenericsFixtures.Compiled(className, call.Method).ReturnType }).ToList();
    var dotNet = typed.Select(call => PrototypeFixture.InvokedOutcome(GenericsFixtures.Compiled(className, call.Method), call)).ToList();
    if (stated is not null)
      Assert.That(dotNet, Is.EqualTo(stated), ".NET");

    IReadOnlyList<string> outcomes = this._csharp is not null
      ? [.. typed.Select(GeneratedCSharp.Compile(this._generated.Text, false).Outcome)]
      : Toolchain.NodeOutcomes(Path.GetDirectoryName(this._generated.Path)!, Path.GetFileName(this._generated.Path), typed, true, this._profile!.Mode, this._profile.Global);
    Assert.That(outcomes, Is.EqualTo(dotNet), this._generated.Text);
  }

  /// <summary>IndexOf of [1.0, NaN] and NaN: the fixture's IndexOfNaN.</summary>
  [Then(@"^IndexOf of \[1\.0, NaN\] and NaN returns (\d+)$")]
  public void IndexOfNaN(string index) => this.Agree("SearchDemo", [new("IndexOfNaN", [])], [index]);

  /// <summary>IndexOf of [-0.0] and 0.0: the fixture's IndexOfZero.</summary>
  [Then(@"^IndexOf of \[-0\.0\] and 0\.0 returns (\d+)$")]
  public void IndexOfZero(string index) => this.Agree("SearchDemo", [new("IndexOfZero", [])], [index]);

  /// <summary>IndexOf of [3, 5, 7] and 7, and of [3, 5, 7] and 4: the fixture's IndexOfSeven and IndexOfFour.</summary>
  [Then(@"^IndexOf of \[3, 5, 7\] and 7 returns (\d+), and of \[3, 5, 7\] and 4 returns (-?\d+)$")]
  public void IndexOfIntegers(string found, string missing) => this.Agree("SearchDemo", [new("IndexOfSeven", []), new("IndexOfFour", [])], [found, missing]);

  /// <summary>The definition is declared once and no instantiation of it on its own.</summary>
  [Then("^the output declares " + StepText.Quoted + " once and no method " + StepText.Quoted + "$")]
  public void DeclaredOnce(string declaration, string method) {
    Assert.That(this._generated.Text.Split(declaration).Length, Is.EqualTo(2), this._generated.Text);
    Assert.That(this._generated.Text, Does.Not.Contain(method + "("));
  }

  /// <summary>The ring over long keeps a value no double holds and counts every push.</summary>
  [Then(@"^a Ring<long> of capacity 3 after pushing 1, 2, 3 and 9007199254740993 has Last (\d+) and Count (\d+)$")]
  public void RingOfLongs(string last, string count) => this.Agree("RingDemo", [new("LastLong", []), new("CountLong", [])], [last, count]);

  /// <summary>The ring over double keeps the sign of -0.0.</summary>
  [Then("^a Ring<double> of capacity 2 after pushing 0.5 and -0.0 has a Last whose reciprocal is negative infinity$")]
  public void RingOfDoubles() => this.Agree("RingDemo", [new("ReciprocalOfLastDouble", [])], [PrototypeFixture.Show(double.NegativeInfinity)]);

  /// <summary>The generated file declares both classes.</summary>
  [Then("^the output declares the classes " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void DeclaresClasses(string first, string second) {
    var level = this._profile!.Parsed.Level;
    var keyword = level >= Polypiler.Core.Backends.JavaScript.JavaScriptLevel.Es2015 ? "class " : "function ";
    Assert.That(this._generated.Text, Does.Contain(keyword + first).And.Contain(keyword + second), this._generated.Text);
  }

  /// <summary>The generated file declares both overloads.</summary>
  [Then("^the output declares " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void DeclaresBoth(string first, string second) => Assert.That(this._generated.Text, Does.Contain(first).And.Contain(second), this._generated.Text);

  /// <summary>The C# translation of the JavaScript fixture returns what node returns of the original.</summary>
  [Then(@"^f\(\) returns (-?[\d.]+), as in node$")]
  public void FAsInNode(string value) {
    var copy = Path.Combine(scenario.Workspace.Resolve("."), "original-" + Guid.NewGuid().ToString("N") + ".mjs");
    File.WriteAllText(copy, File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)));
    var call = new PrototypeCall("f", []);
    var node = Toolchain.NodeOutcomes(Path.GetDirectoryName(copy)!, Path.GetFileName(copy), [call], true);
    var expected = PrototypeFixture.Show(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
    Assert.That(node, Is.EqualTo(new[] { expected }), "node");
    Assert.That(GeneratedCSharp.Compile(this._generated.Text, false).Outcome(call), Is.EqualTo(expected), this._generated.Text);
  }

  /// <summary>default(T) of int, double, bool and the class Box.</summary>
  [Then("^Def<int> returns 0, Def<double> returns 0, Def<bool> returns false and Def<Box> of the fixture's class Box returns null$")]
  public void Defaults() => this.Agree("SearchDemo", [new("DefInt", []), new("DefDouble", []), new("DefBool", []), new("DefBoxIsNull", [])],
    ["0", PrototypeFixture.Show(0.0), "false", "true"]);

  /// <summary>new T() of the class Box makes a new object each call.</summary>
  [Then(@"^Make<Box>\(\) returns a new Box each call$")]
  public void NewEachCall() => this.Agree("SearchDemo", [new("MakeBoxIsNew", []), new("MakeBoxValue", [])], ["true", "0"]);

  /// <summary>The exit code and the diagnostic of the refusal.</summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + "$")]
  public void ExitCodeAndCode(int exitCode, string code) {
    Assert.That(scenario.Log.Last.ExitCode, Is.EqualTo(exitCode), scenario.Log.Last.Output);
    Assert.That(scenario.Log.Last.Output, Does.Contain(code));
  }

  /// <summary>The exit code, the diagnostic and what it names.</summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void ExitCodeCodeAndSubject(int exitCode, string code, string subject) {
    this.ExitCodeAndCode(exitCode, code);
    Assert.That(scenario.Log.Last.Output, Does.Contain(subject));
  }

  /// <summary>A refused translation writes nothing.</summary>
  [Then("^no file is written$")]
  public void NothingWritten() => Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);

}
