using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/prototype/functions-and-closures.feature</c> (#1326). The expected
/// value is never computed here: it is what .NET computes of the original C# fixture, built into
/// this assembly, or what <c>node</c> computes of the original JavaScript; the step's own numbers are
/// checked against that oracle too.
/// </summary>
[Binding, Scope(Feature = "Delegates, lambdas, local functions and closures")]
public sealed class FunctionsSteps(PrototypeScenario scenario) {

  private (string Path, string Text) _generated;
  private ProfileCase? _profile;
  private CSharpProfile? _csharp;
  private readonly Dictionary<string, (string Path, string Text)> _translations = [];

  /// <summary>The profile of a level in the module format the level starts with: an ES module, or CommonJS at ECMAScript 5.</summary>
  private static ProfileCase Level(string level) => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

  private static PrototypeCall Call(string method, params object[] arguments)
    => new(method, arguments) { Returns = FunctionsFixtures.Compiled("Closures", method).ReturnType };

  /// <summary>What .NET computes of the C# fixture.</summary>
  private static string DotNet(PrototypeCall call) => PrototypeFixture.InvokedOutcome(FunctionsFixtures.Compiled("Closures", call.Method), call);

  /// <summary>What node computes of a module for calls.</summary>
  private static IReadOnlyList<string> Node((string Path, string Text) module, IReadOnlyList<PrototypeCall> calls, ProfileCase? profile = null)
    => Toolchain.NodeOutcomes(Path.GetDirectoryName(module.Path)!, Path.GetFileName(module.Path), calls, true, profile?.Mode ?? "native", profile?.Global);

  /// <summary>A copy of the scenario's JavaScript source as an ES module that node can load.</summary>
  private (string Path, string Text) Original(string source) {
    var copy = Path.Combine(scenario.Workspace.Resolve("."), "original-" + Guid.NewGuid().ToString("N") + ".mjs");
    File.WriteAllText(copy, File.ReadAllText(scenario.Workspace.Resolve(source)));
    return (copy, File.ReadAllText(copy));
  }

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
      return;
    }

    this._profile = Level(name);
    this._generated = scenario.Translate(scenario.Source, from, "javascript", this._profile.Arguments);
  }

  /// <summary>The loops of the C# fixture in node, as .NET computes them.</summary>
  [Then(@"^in node ForCapture\(\) returns (\d+) and ForeachCapture\(\[0, 1, 2\]\) returns (\d+)$")]
  public void LoopsInNode(string shared, string fresh) {
    PrototypeCall[] calls = [Call("ForCapture"), Call("ForeachCapture", new[] { 0, 1, 2 })];
    Assert.That(calls.Select(call => DotNet(call).Split(" | ")[0]), Is.EqualTo(new[] { shared, fresh }), ".NET");
    Assert.That(Node(this._generated, calls, this._profile).Select(outcome => outcome.Split(" | ")[0]), Is.EqualTo(new[] { shared, fresh }), this._generated.Text);
  }

  /// <summary>The generated JavaScript has no problem at its level.</summary>
  [Then("^the output parses at the level of (.+)$")]
  public void ParsesAtTheLevel(string level)
    => Assert.That(GeneratedJavaScript.Problems(this._generated.Text, Level(level).Parsed), Is.Empty, this._generated.Text);

  /// <summary>The loops of the JavaScript fixture in the generated C#, as node computes them of the original.</summary>
  [Then(@"^compiled for the running runtime forVar\(\) returns (\d+), forLet\(\) returns (\d+) and forLetAssigned\(\) returns (\d+)$")]
  public void LetLoopsCompiled(string shared, string fresh, string assigned) {
    PrototypeCall[] calls = [new("forVar", []), new("forLet", []), new("forLetAssigned", [])];
    Assert.That(Node(this.Original(scenario.Source), calls), Is.EqualTo(new[] { shared, fresh, assigned }), "node");
    var build = GeneratedCSharp.Compile(this._generated.Text, false);
    Assert.That(calls.Select(build.Outcome), Is.EqualTo(new[] { shared, fresh, assigned }), this._generated.Text);
  }

  /// <summary>The generated C# compiles against the reference assemblies of the profile at its language version.</summary>
  [Then("^the generated file compiles against the reference assemblies of (.+) without diagnostics$")]
  public void CompilesForTheProfile(string name) {
    var profile = CSharpProfile.Parse(name);
    Assert.That(GeneratedCSharp.ComplaintsFor(this._generated.Text, profile.Name, profile.LanguageVersionName), Is.Empty, this._generated.Text);
  }

  /// <summary>Both fixtures of the folder as the sources of the scenario.</summary>
  [Given("^the fixtures " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void BothFixtures(string csharp, string javascript) {
    foreach (var path in new[] { csharp, javascript })
      scenario.Workspace.Copy(FunctionsFixtures.PathOf(Path.GetFileName(path)), path);

    scenario.Source = csharp;
    scenario.Text = javascript;
  }

  /// <summary>The C# fixture to JavaScript and the JavaScript fixture to C#, both with the default profile.</summary>
  [When("^each is translated to the other language with the default profile$")]
  public void EachTranslated() {
    this._translations["csharp"] = scenario.Translate(scenario.Source, "csharp", "javascript");
    this._translations["javascript"] = scenario.Translate(scenario.Text, "javascript", "csharp");
  }

  /// <summary>The closures of both fixtures: .NET and node on the originals, node and .NET on the translations.</summary>
  [Then(@"^Counter\(\) returns (\d+), Interleaved\(\) returns (\d+), Late\(2\) returns (\d+), Fact\(5\) returns (\d+), Fact\(0\) returns (\d+) and Composed\(4\) returns (\d+) in \.NET, in node and in both generated files$")]
  public void ClosuresEverywhere(string counter, string interleaved, string late, string fact, string one, string composed) {
    string[] stated = [counter, interleaved, late, fact, one, composed];
    PrototypeCall[] csharp = [Call("Counter"), Call("Interleaved"), Call("Late", 2), Call("Fact", 5), Call("Fact", 0), Call("Composed", 4)];
    Assert.That(csharp.Select(DotNet), Is.EqualTo(stated), ".NET");
    Assert.That(Node(this._translations["csharp"], csharp), Is.EqualTo(stated), this._translations["csharp"].Text);
    PrototypeCall[] javascript = [new("counter", []), new("interleaved", []), new("late", [2.0]), new("fact", [5.0]), new("fact", [0.0]), new("composed", [4.0])];
    var numbers = stated.Select(value => PrototypeFixture.Show(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture))).ToList();
    Assert.That(Node(this.Original(scenario.Text), javascript), Is.EqualTo(numbers), "node");
    var build = GeneratedCSharp.Compile(this._translations["javascript"].Text, false);
    Assert.That(javascript.Select(build.Outcome), Is.EqualTo(numbers), this._translations["javascript"].Text);
  }

  /// <summary>A method group of the C# fixture in the generated JavaScript.</summary>
  [Then(@"^MethodGroup\(-7\) of the C# fixture returns (\d+) in the generated JavaScript$")]
  public void MethodGroup(string squared) {
    var call = Call("MethodGroup", -7);
    Assert.That(DotNet(call), Is.EqualTo(squared), ".NET");
    Assert.That(Node(this._translations["csharp"], [call]), Is.EqualTo(new[] { squared }), this._translations["csharp"].Text);
  }

  /// <summary>Translates the source to C# for a profile.</summary>
  [When("^it is translated to C# with " + StepText.Quoted + "$")]
  public void ToCSharp(string options) {
    this._csharp = CSharpProfile.Parse(CommandLine.Split(options)[1]);
    this._generated = scenario.Translate(scenario.Source, "csharp", "csharp", "--profile", this._csharp.Name);
    this.CompilesForTheProfile(this._csharp.Name);
  }

  /// <summary>Translates the source to JavaScript for a level.</summary>
  [When("^it is translated to JavaScript with " + StepText.Quoted + "$")]
  public void ToJavaScript(string options) {
    this._profile = Level(CommandLine.Split(options)[1]);
    this._generated = scenario.Translate(scenario.Source, "csharp", "javascript", this._profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(this._generated.Text, this._profile.Parsed), Is.Empty, this._generated.Text);
  }

  /// <summary>The forms of C# 2: anonymous methods and a nested delegate type, no lambda and no Func.</summary>
  [Then("^the output contains " + StepText.Quoted + " and a nested delegate type " + StepText.Quoted + " and no " + StepText.Quoted + " and no " + StepText.Quoted + "$")]
  public void CSharp2Forms(string anonymous, string type, string lambda, string func) {
    var text = this._generated.Text;
    Assert.That(text, Does.Contain(StepText.Unescape(anonymous)).And.Contain($"delegate int {type}(").And.Not.Contain(StepText.Unescape(lambda)).And.Not.Contain(func), text);
  }

  /// <summary>The forms of C# 3: Func and lambdas, no local function.</summary>
  [Then("^the output contains " + StepText.Quoted + " and " + StepText.Quoted + " and no local function$")]
  public void CSharp3Forms(string func, string lambda) {
    var text = this._generated.Text;
    Assert.That(text, Does.Contain(func).And.Contain(StepText.Unescape(lambda)), text);
    Assert.That(text, Does.Not.Contain("int F(int m)"), text);
  }

  /// <summary>The forms of C# 14: a local function and static lambdas where nothing is captured.</summary>
  [Then("^Fact is written with a local function and every lambda without captures is " + StepText.Quoted + "$")]
  public void CSharp14Forms(string modifier) {
    var text = this._generated.Text;
    Assert.That(text, Does.Contain("int F(int m) => "), text);
    Assert.That(text, Does.Contain($"Compose({modifier} v => v + 1, {modifier} v => v * 3)").And.Contain($"f = {modifier} () => 1;"), text);
    Assert.That(text, Does.Contain("fs[k++] = () => i;"), "a lambda that captures is not static");
  }

  /// <summary>The forms of ECMAScript 5: no arrow, no let, no const.</summary>
  [Then("^the output contains no " + StepText.Quoted + ", no " + StepText.Quoted + " and no " + StepText.Quoted + "$")]
  public void Es5Forms(string arrow, string let, string constant) {
    var text = this._generated.Text;
    Assert.That(text, Does.Not.Contain(StepText.Unescape(arrow)).And.Not.Contain(let + " ").And.Not.Contain(constant + " "), text);
  }

  /// <summary>The fixture and the one method of it the scenario is about.</summary>
  [Given("^the fixture " + StepText.Quoted + " and its method (\\w+)$")]
  public void FixtureMethod(string path, string method) {
    scenario.Workspace.Copy(FunctionsFixtures.PathOf(Path.GetFileName(path)), path);
    scenario.Source = path;
    scenario.Text = method;
  }

  /// <summary>The C# fixture in JavaScript, its method called in node.</summary>
  [When(@"^it is translated to JavaScript and (\w+)\(\) is called in node$")]
  public void CalledInNode(string method) {
    this._profile = Level("es2022");
    this._generated = scenario.Translate(scenario.Source, "csharp", "javascript", this._profile.Arguments);
    Assert.That(method, Is.EqualTo(scenario.Text));
  }

  /// <summary>The method throws in node what the trap of a null reference is today, and .NET throws its NullReferenceException.</summary>
  [Then(@"^it throws as the NullReference trap of #1323, where \.NET throws System\.NullReferenceException$")]
  public void ThrowsInNode() {
    Assert.That(DotNet(Call(scenario.Text)), Is.EqualTo(PrototypeFixture.Trap(IrTrap.NullReference)), ".NET");
    var script = $"try {{ {scenario.Text}(); console.log('none'); }} catch (error) {{ console.log(error.constructor.name); }}";
    Assert.That(ClassesSteps.Node(this._generated.Path, true, scenario.Text, script), Is.EqualTo("TypeError"), this._generated.Text);
  }

  /// <summary>The JavaScript fixture's callNull throws TypeError in node and its C# translation NullReferenceException.</summary>
  [Then(@"^the JavaScript function (\w+)\(\) of " + StepText.Quoted + @", which throws TypeError " + StepText.Quoted + @" in node, translated to C# throws System\.NullReferenceException$")]
  public void ThrowsInCSharp(string function, string path, string message) {
    scenario.Workspace.Copy(FunctionsFixtures.PathOf(Path.GetFileName(path)), path);
    var original = this.Original(path);
    var script = $"try {{ {function}(); console.log('none'); }} catch (error) {{ console.log(error.constructor.name + ': ' + error.message); }}";
    Assert.That(ClassesSteps.Node(original.Path, true, function, script), Is.EqualTo("TypeError: " + message), "node");
    var generated = scenario.Translate(path, "javascript", "csharp");
    Assert.That(GeneratedCSharp.Compile(generated.Text, false).Outcome(new PrototypeCall(function, [])), Is.EqualTo(PrototypeFixture.Trap(IrTrap.NullReference)), generated.Text);
  }

  /// <summary>A JavaScript function of the scenario's own as an ES module.</summary>
  [Given("^the JavaScript function " + StepText.Quoted + "$")]
  public void JavaScriptFunction(string text) => scenario.Script("module.js", StepText.Unescape(text));

  /// <summary>Runs the translate command on the source to the other language.</summary>
  [When("^I run the translate command to (C#|JavaScript)$")]
  public void RunTranslate(string target)
    => scenario.Polypiler.Run(["translate", scenario.Source, "--from", Path.GetExtension(scenario.Source) == ".cs" ? "csharp" : "javascript",
      "--to", target == "C#" ? "csharp" : "javascript", "--output", "out"]);

  /// <summary>The exit code, the diagnostic and what it names; nothing is written.</summary>
  [Then("^the exit code is (\\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + " and nothing is written$")]
  public void Refused(int exitCode, string code, string subject) {
    Assert.That(scenario.Log.Last.ExitCode, Is.EqualTo(exitCode), scenario.Log.Last.Output);
    Assert.That(scenario.Log.Last.Output, Does.Contain(code).And.Contain(subject));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

}
