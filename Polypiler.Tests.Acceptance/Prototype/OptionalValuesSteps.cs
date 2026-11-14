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
/// The steps of <c>tests/features/prototype/optional-values.feature</c>. The expected value is never
/// computed here: it is what .NET computes of the original C# - the fixture built into this assembly
/// or the snippet compiled - or what <c>node</c> computes of the original JavaScript.
/// </summary>
[Binding, Scope(Feature = "Optional values and null")]
public sealed class OptionalValuesSteps(PrototypeScenario scenario) {

  private (string Path, string Text) _generated;
  private CSharpProfile? _csharp;
  private ProfileCase? _javascript;
  private readonly List<(string Name, string Path, string Text)> _outputs = [];

  /// <summary>The profile of a level in the module format the level starts with: CommonJS at ECMAScript 5, an ES module above.</summary>
  private static ProfileCase Level(string level) => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

  /// <summary>Translates the source with the options of the step, which name the target and the profile.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var to = arguments[arguments.IndexOf("--to") + 1];
    var profile = arguments[arguments.IndexOf("--profile") + 1];
    var from = Path.GetExtension(scenario.Source) == ".cs" ? "csharp" : "javascript";
    if (to == "csharp") {
      this._csharp = CSharpProfile.Parse(profile);
      this._generated = scenario.Translate(scenario.Source, from, to, "--profile", profile);
      Assert.That(GeneratedCSharp.ComplaintsFor(this._generated.Text, this._csharp.Name, this._csharp.LanguageVersionName), Is.Empty, this._generated.Text);
      return;
    }

    this._javascript = Level(profile);
    this._generated = scenario.Translate(scenario.Source, from, "javascript", this._javascript.Arguments);
    Assert.That(GeneratedJavaScript.Problems(this._generated.Text, this._javascript.Parsed), Is.Empty, this._generated.Text);
  }

  /// <summary>What the generated file computes for calls: compiled for C#, in node for JavaScript.</summary>
  private IReadOnlyList<string> Generated(IReadOnlyList<PrototypeCall> calls) {
    if (this._javascript is { } profile)
      return Toolchain.NodeOutcomes(Path.GetDirectoryName(this._generated.Path)!, Path.GetFileName(this._generated.Path), calls, false, profile.Mode, profile.Global);

    var build = GeneratedCSharp.Compile(this._generated.Text, false);
    return [.. calls.Select(build.Outcome)];
  }

  /// <summary>Calls of the fixture: what .NET computes of the original must be what the step says, and the generated file must compute it too.</summary>
  private void Expect(IReadOnlyList<PrototypeCall> calls, params string[] expected) {
    Assert.That(calls.Select(call => PrototypeFixture.InvokedOutcome(OptionalFixtures.Compiled(call.Method), call)), Is.EqualTo(expected), ".NET");
    Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(OptionalFixtures.Imported(), call)), Is.EqualTo(expected), "the interpreter");
    Assert.That(this.Generated(calls), Is.EqualTo(expected), this._generated.Text);
  }

  private static PrototypeCall Call(string method, params object?[] arguments) => new(method, arguments!) { Returns = OptionalFixtures.Compiled(method).ReturnType };

  [Then(@"^Find\(\[4, 5, 6\], 6\) \?\? -1 returns (-?\d+) and Find\(\[4, 5, 6\], 7\) \?\? -1 returns (-?\d+)$")]
  public void FindOr(string found, string missing)
    => this.Expect([Call("FindOr", new[] { 4, 5, 6 }, 6), Call("FindOr", new[] { 4, 5, 6 }, 7)], found + " | [4,5,6]", missing + " | [4,5,6]");

  [Then(@"^with int\? a = null, \(a \+ 1\) \?\? -1 returns (-?\d+), a < 1 returns (\w+) and a == null returns (\w+)$")]
  public void Lifted(string sum, string less, string isNull)
    => this.Expect([Call("Next", [null]), Call("Less", [null]), Call("IsNull", [null])], sum, less, isNull);

  [Then(@"^with int\? z = 0, z \?\? 5 returns (\d+)$")]
  public void Zero(string value) => this.Expect([Call("Or5", 0)], value);

  [Then(@"^with int\? q = null, q \?\?= 7 then q \?\?= 9 leaves q at (\d+)$")]
  public void Assigned(string value) => this.Expect([Call("Assigned")], value);

  /// <summary>The snippet's method in node, against what .NET computes of the snippet.</summary>
  [Then(@"^L\(null\) returns (-?\d+) and L\(new int\[3\]\) returns (\d+)$")]
  public void NullConditional(string absent, string length) {
    PrototypeCall[] calls = [new("L", [null!]), new("L", [new int[3]])];
    var original = GeneratedCSharp.Compile(File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)), false);
    Assert.That(calls.Select(original.Outcome), Is.EqualTo(new[] { absent, length + " | [0,0,0]" }), ".NET");
    Assert.That(this.Generated(calls), Is.EqualTo(new[] { absent, length + " | [0,0,0]" }), this._generated.Text);
  }

  /// <summary>The code of the generated file - not its header - has neither operator.</summary>
  [Then("^the output contains neither " + StepText.Quoted + " nor " + StepText.Quoted + "$")]
  public void Neither(string first, string second) {
    var code = string.Join('\n', this._generated.Text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    Assert.That(code, Does.Not.Contain(first).And.Not.Contain(second), this._generated.Text);
  }

  /// <summary>Writes a JavaScript module into the workspace as the source.</summary>
  [Given("^the JavaScript function " + StepText.Quoted + "$")]
  public void JavaScriptFunction(string text) => scenario.Script("module.mjs", StepText.Unescape(text));

  [When("^it is translated to C# with " + StepText.Quoted + "$")]
  public void ToCSharp(string options) {
    var profile = CommandLine.Split(options)[1];
    this._csharp = CSharpProfile.Parse(profile);
    this._generated = scenario.Translate(scenario.Source, "javascript", "csharp", "--profile", profile);
    Assert.That(GeneratedCSharp.ComplaintsFor(this._generated.Text, this._csharp.Name, this._csharp.LanguageVersionName), Is.Empty, this._generated.Text);
  }

  /// <summary>What node prints of the original module for calls of one function.</summary>
  private string Original(string function, params string[] arguments)
    => ClassesSteps.Node(scenario.Workspace.Resolve(scenario.Source), true, function, $"console.log([{string.Join(", ", arguments.Select(argument => $"{function}({argument})"))}].map(String).join(' '));");

  [Then(@"^f\((-?\d+)\) returns (-?\d+) and f\((-?\d+)\) returns (-?\d+), as in node$")]
  public void CoercedLikeNode(int first, string firstResult, int second, string secondResult) {
    Assert.That(this.Original("f", first.ToString(System.Globalization.CultureInfo.InvariantCulture), second.ToString(System.Globalization.CultureInfo.InvariantCulture)), Is.EqualTo($"{firstResult} {secondResult}"), "node");
    var method = GeneratedCSharp.Compile(this._generated.Text, false).Method("f");
    string Run(int argument) => ((double)method.Invoke(null, [(double)argument])!).ToString(System.Globalization.CultureInfo.InvariantCulture);
    Assert.That($"{Run(first)} {Run(second)}", Is.EqualTo($"{firstResult} {secondResult}"), this._generated.Text);
  }

  [When("^it is translated to JavaScript and to C# with every profile$")]
  public void EveryProfile() {
    foreach (var profile in ProfileCase.All) {
      var generated = scenario.Translate(scenario.Source, "csharp", "javascript", profile.Arguments);
      this._outputs.Add((profile.ToString(), generated.Path, generated.Text));
    }

    foreach (var profile in CSharpProfile.All) {
      var generated = scenario.Translate(scenario.Source, "csharp", "csharp", "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(generated.Text, profile.Name, profile.LanguageVersionName), Is.Empty, generated.Text);
      this._outputs.Add((profile.Name, generated.Path, generated.Text));
    }
  }

  [Then("^V\\(\\) throws in every output, in C# a System.InvalidOperationException with the message " + StepText.Quoted + "$")]
  public void ThrowsEverywhere(string message) {
    var call = new PrototypeCall("V", []);
    var trap = PrototypeFixture.Trap(IrTrap.MissingValue);
    var original = GeneratedCSharp.Compile(File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)), false).Method("V");
    var thrown = Assert.Throws<System.Reflection.TargetInvocationException>(() => original.Invoke(null, []))!.InnerException!;
    Assert.That((thrown.GetType(), thrown.Message), Is.EqualTo((typeof(InvalidOperationException), message)), ".NET");
    Assert.That(PrototypeFixture.InterpretedOutcome(PrototypeFixture.Import("Snippet.cs", File.ReadAllText(scenario.Workspace.Resolve(scenario.Source))), call), Is.EqualTo(trap), "the interpreter");
    foreach (var (name, path, text) in this._outputs) {
      if (path.EndsWith(".cs", StringComparison.Ordinal)) {
        var generated = Assert.Throws<System.Reflection.TargetInvocationException>(() => GeneratedCSharp.Compile(text, false).Method("V").Invoke(null, []))!.InnerException!;
        Assert.That((generated.GetType(), generated.Message), Is.EqualTo((typeof(InvalidOperationException), message)), name);
        continue;
      }

      var profile = ProfileCase.All.Single(candidate => candidate.ToString() == name);
      Assert.That(Toolchain.NodeOutcomes(Path.GetDirectoryName(path)!, Path.GetFileName(path), [call], false, profile.Mode, profile.Global).Single(), Is.EqualTo(trap), name);
    }
  }

  [When("^it is translated to C# and to JavaScript with " + StepText.Quoted + "$")]
  public void ToBoth(string options) {
    var level = CommandLine.Split(options)[1];
    this._javascript = Level(level);
    this._generated = scenario.Translate(scenario.Source, "javascript", "javascript", this._javascript.Arguments);
    var csharp = scenario.Translate(scenario.Source, "javascript", "csharp");
    this._outputs.Add(("csharp", csharp.Path, csharp.Text));
  }

  /// <summary>
  /// The JavaScript output returns <c>undefined</c> where the source did, as node does of the source;
  /// C# has one absent value, so the C# output returns <c>null</c> there.
  /// </summary>
  [Then(@"^g\((\d+)\) returns undefined, not null, as in node, and the C# method returns null, the one absent value of C#$")]
  public void KeepsUndefined(int argument) {
    var script = $"const r = g({argument}); console.log(r === undefined ? 'undefined' : r === null ? 'null' : String(r));";
    Assert.That(ClassesSteps.Node(scenario.Workspace.Resolve(scenario.Source), true, "g", script), Is.EqualTo("undefined"), "node");
    Assert.That(ClassesSteps.Node(this._generated.Path, true, "g", script), Is.EqualTo("undefined"), this._generated.Text);
    var csharp = this._outputs.Single(output => output.Name == "csharp").Text;
    Assert.That(GeneratedCSharp.Compile(csharp, false).Method("g").Invoke(null, [(double)argument]), Is.Null, csharp);
  }

  /// <summary>Copies a fixture of the folder and checks that it holds the function the step names.</summary>
  [Given("^the fixture " + StepText.Quoted + " with " + StepText.Quoted + "$")]
  public void FixtureWith(string path, string function) {
    scenario.Workspace.Copy(OptionalFixtures.PathOf(Path.GetFileName(path)), path);
    scenario.Source = path;
    Assert.That(File.ReadAllText(scenario.Workspace.Resolve(path)), Does.Contain(StepText.Unescape(function)));
  }

  [When("^I run the translate command with " + StepText.Quoted + "$")]
  public void TranslateCommand(string options)
    => scenario.Polypiler.Run(["translate", scenario.Source, "--from", "javascript", .. CommandLine.Split(options), "--output", "out"]);

  /// <summary>The command was refused with the code and what it names, and wrote nothing.</summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Refused(int exitCode, string code, string subject) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
    Assert.That(result.Output, Does.Contain(code).And.Contain(subject));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

}
