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
/// The steps of <c>tests/features/ir/ir-exceptions.feature</c>. The expected value is never computed
/// here: it is what .NET computes of the original C# fixture, built into this assembly, or what
/// <c>node</c> computes of the original JavaScript; a literal of the scenario is held to those first.
/// </summary>
[Binding, Scope(Feature = "Exceptions and traps")]
public sealed class ExceptionSteps(PrototypeScenario scenario) {

  /// <summary>Each translation of the scenario: the generated file, and the JavaScript profile it was written for or <see langword="null"/> for C#.</summary>
  private readonly List<(string Path, string Text, ProfileCase? Profile)> _generated = [];

  /// <summary>The command lines a refusal step ran, with what each gave.</summary>
  private readonly List<CommandResult> _refusals = [];

  /// <summary>The profile of a level in the module format the level starts with: an ES module, or CommonJS at ECMAScript 5.</summary>
  private static ProfileCase Level(string level) => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

  private string From => Path.GetExtension(scenario.Source) == ".cs" ? "csharp" : "javascript";

  /// <summary>A fixture of the folder with the scenario's own description of it.</summary>
  [Given("^the fixture " + StepText.Quoted + " (?:with|whose) .+$")]
  public void DescribedFixture(string path) {
    scenario.Workspace.Copy(ExceptionFixtures.PathOf(Path.GetFileName(path)), path);
    scenario.Source = path;
  }

  /// <summary>A JavaScript function of the scenario's own as the source.</summary>
  [Given("^the JavaScript function " + StepText.Quoted + "$")]
  public void JavaScriptFunction(string text) => scenario.Script("snippet.js", StepText.Unescape(text));

  /// <summary>Translates the source to C# for a profile, which compiles at its language version.</summary>
  private void ToCSharp(string name) {
    var profile = CSharpProfile.Parse(name);
    var (path, text) = scenario.Translate(scenario.Source, this.From, "csharp", "--profile", profile.Name);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    this._generated.Add((path, text, null));
  }

  /// <summary>Translates the source to JavaScript for a level, at which the file is.</summary>
  private void ToJavaScript(string level) {
    var profile = Level(level);
    var (path, text) = scenario.Translate(scenario.Source, this.From, "javascript", profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    this._generated.Add((path, text, profile));
  }

  /// <summary>Translates the source with the options of the step, which name the target and its profile.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var to = arguments[arguments.IndexOf("--to") + 1];
    var name = arguments[arguments.IndexOf("--profile") + 1];
    if (to == "csharp")
      this.ToCSharp(name);
    else
      this.ToJavaScript(name);
  }

  /// <summary>Translates the source to both languages.</summary>
  [When("^it is translated to JavaScript with \"--profile (\\S+)\" and to C# with \"--profile (\\S+)\"$")]
  public void TranslatedToBoth(string level, string profile) {
    this.ToJavaScript(level);
    this.ToCSharp(profile);
  }

  /// <summary>Translates the source to C# for a profile.</summary>
  [When("^it is translated to C# with \"--profile (\\S+)\"$")]
  public void TranslatedToCSharp(string profile) => this.ToCSharp(profile);

  /// <summary>Runs the two translations the step names, each of which is expected to be refused.</summary>
  [When("^it is translated with " + StepText.Quoted + " or " + StepText.Quoted + "$")]
  public void TranslatedWithEither(string first, string second) {
    foreach (var options in new[] { first, second })
      this._refusals.Add(scenario.Polypiler.Run(["translate", scenario.Source, "--from", this.From, "--output", "out-" + Guid.NewGuid().ToString("N"), .. CommandLine.Split(options)]));
  }

  /// <summary>Runs the translate command on the source with the options of the step.</summary>
  [When("^I run the translate command with " + StepText.Quoted + "$")]
  public void TranslateCommandWith(string options)
    => scenario.Polypiler.Run(["translate", scenario.Source, "--from", this.From, "--output", "out", .. CommandLine.Split(options)]);

  /// <summary>The outcome of calls in .NET of the original fixture, held to the scenario's values, and in every translation of the scenario.</summary>
  private void Everywhere(string className, IReadOnlyList<PrototypeCall> calls, IReadOnlyList<string> expected) {
    Assert.That(calls.Select(call => PrototypeFixture.InvokedOutcome(ExceptionFixtures.Compiled(className, call.Method), call)), Is.EqualTo(expected), ".NET");
    var imported = ExceptionFixtures.Imported(Path.GetFileNameWithoutExtension(scenario.Source));
    Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(imported, call)), Is.EqualTo(expected), "the interpreter");
    foreach (var (path, text, profile) in this._generated) {
      var outcomes = profile is null
        ? calls.Select(GeneratedCSharp.Compile(text, false).Outcome).ToList()
        : Toolchain.NodeOutcomes(Path.GetDirectoryName(path)!, Path.GetFileName(path), calls, false, profile.Mode, profile.Global);
      Assert.That(outcomes, Is.EqualTo(expected), $"{profile?.ToString() ?? "C#"}\n{text}");
    }
  }

  /// <summary>Every trap selects the clause of its class.</summary>
  [Then(@"^Classify returns (\d+), (\d+), (\d+), (\d+), (\d+) and (\d+) for k = 0, 1, 2, 3, 4 and 5$")]
  public void Classify(string a, string b, string c, string d, string e, string f)
    => this.Everywhere("TrapsDemo", [.. Enumerable.Range(0, 6).Select(k => new PrototypeCall("Classify", [k]))], [a, b, c, d, e, f]);

  /// <summary>An invalid cast and a field read through null each select their own clause.</summary>
  [Then(@"^Cast returns (\d+) for k = 0 and (\d+) for k = 1$")]
  public void Cast(string cast, string empty) => this.Everywhere("TrapsDemo", [new("Cast", [0]), new("Cast", [1])], [cast, empty]);

  /// <summary>The message of an exception and of its inner exception.</summary>
  [Then("^Chain\\(\\) returns " + StepText.Quoted + "$")]
  public void Chain(string expected) => this.Everywhere("ClassesDemo", [new("Chain", [])], [expected]);

  /// <summary>The filter selects its clause for one message, the clause after it for another.</summary>
  [Then(@"^Filter\(""x""\) returns (\d+) and Filter\(""y""\) returns (\d+)$")]
  public void Filter(string x, string y) => this.Everywhere("ClassesDemo", [new("Filter", ["x"]), new("Filter", ["y"])], [x, y]);

  /// <summary>The value of a return is computed before the finally block writes the field.</summary>
  [Then(@"^Fin\(\) returns (\d+) and leaves the static field at (\d+)$")]
  public void Fin(string value, string field) => this.Everywhere("ClassesDemo", [new("Fin", []), new("FinState", [])], [value, field]);

  /// <summary>
  /// The message of an exception made without one names its class. .NET names the fixture's class with
  /// its namespace; the IR and the generated code, which have none, with its name.
  /// </summary>
  [Then("^new X\\(\\)\\.Message is " + StepText.Quoted + "$")]
  public void DefaultMessage(string expected) {
    var call = new PrototypeCall("DefaultMessage", []);
    Assert.That(PrototypeFixture.InvokedOutcome(ExceptionFixtures.Compiled("ClassesDemo", call.Method), call), Is.EqualTo(expected.Replace("'X'", $"'{ExceptionFixtures.Namespace}.X'", StringComparison.Ordinal)), ".NET");
    Assert.That(PrototypeFixture.InterpretedOutcome(ExceptionFixtures.Imported("Classes"), call), Is.EqualTo(expected), "the interpreter");
    foreach (var (path, text, profile) in this._generated) {
      var outcome = profile is null ? GeneratedCSharp.Compile(text, false).Outcome(call)
        : Toolchain.NodeOutcomes(Path.GetDirectoryName(path)!, Path.GetFileName(path), [call], false, profile.Mode, profile.Global)[0];
      Assert.That(outcome, Is.EqualTo(expected), text);
    }
  }

  /// <summary>The order of the filter, the inner finally block and the handler.</summary>
  [Then("^running it logs " + StepText.Quoted + ", as \\.NET does$")]
  public void Logs(string expected) => this.Everywhere("OrderDemo", [new("Run", [])], [expected]);

  /// <summary>Both translations of the step were refused, each with its code and the reason.</summary>
  [Then("^the exit code is (\\d+) and the output contains " + StepText.Quoted + " or " + StepText.Quoted + " respectively and " + StepText.Quoted + "$")]
  public void BothRefused(int exitCode, string first, string second, string reason) {
    Assert.That(this._refusals, Has.Count.EqualTo(2));
    foreach (var (result, code) in this._refusals.Zip([first, second])) {
      Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
      Assert.That(result.Output, Does.Contain(code).And.Contain(reason));
    }
  }

  /// <summary>The JavaScript fixture translated to C# computes what node computes of the original.</summary>
  [Then(@"^f\(0\) returns (\d+) and f\(1\) returns (\d+), as in node$")]
  public void AsInNode(string zero, string one) {
    List<PrototypeCall> calls = [new("f", [0]), new("f", [1])];
    var original = Path.Combine(scenario.Workspace.Resolve("."), "original-" + Guid.NewGuid().ToString("N") + ".mjs");
    File.WriteAllText(original, File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)));
    Assert.That(Toolchain.NodeOutcomes(Path.GetDirectoryName(original)!, Path.GetFileName(original), calls), Is.EqualTo(new[] { zero, one }), "node");
    var (_, text, _) = this._generated.Single();
    Assert.That(calls.Select(GeneratedCSharp.Compile(text, false).Outcome), Is.EqualTo(new[] { zero, one }), text);
  }

  /// <summary>The translation was refused with its code and the reason, and nothing was written.</summary>
  [Then("^the exit code is (\\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Refused(int exitCode, string code, string reason) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
    Assert.That(result.Output, Does.Contain(code).And.Contain(reason));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

  /// <summary>The error a trap throws in node, as the scenario describes it: its chain, name, message and text.</summary>
  [Then(@"^Div\((-?\d+), (-?\d+)\) throws an error e with e instanceof RangeError (true|false), e\.name " + StepText.Quoted + ", e\\.message " + StepText.Quoted + " and String\\(e\\) " + StepText.Quoted + "$")]
  public void DivThrows(int a, int b, string range, string name, string message, string text) {
    Assert.That(() => _ = a / b, Throws.TypeOf<DivideByZeroException>(), ".NET");
    var (path, generated, profile) = this._generated.Single();
    var shown = ClassesSteps.Node(path, profile!.Module == "esm", "Div",
      $"try {{ Div({a}, {b}); console.log('none'); }} catch (e) {{ console.log(JSON.stringify([e instanceof RangeError, e.name, e.message, String(e)])); }}");
    Assert.That(shown, Is.EqualTo($"[{range},\"{name}\",\"{message}\",\"{text}\"]"), generated);
  }

}
