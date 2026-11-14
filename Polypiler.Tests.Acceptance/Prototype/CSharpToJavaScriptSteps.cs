using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/prototype/csharp-to-javascript.feature</c> and
/// <c>csharp-to-javascript-wide.feature</c>: the generated module is run by <c>node</c>, and the
/// expected outcome of a call is never computed here but is what the fixture does after the C#
/// compiler built it into this assembly, or what the interpreter does with the imported IR.
/// </summary>
[Binding]
[Scope(Feature = "Translate a small C# algorithm to JavaScript")]
[Scope(Feature = "Translate wider C# to JavaScript")]
public sealed class CSharpToJavaScriptSteps(PrototypeScenario scenario) {

  private PrototypeCall? _call;
  private string _outcome = "";
  private string _table = "";

  private string Module => PrototypeFixture.ClassOf(Path.GetFileNameWithoutExtension(scenario.Source)) + ".mjs";

  private string Output => scenario.Workspace.Resolve("out");

  /// <summary>The module the source was imported into, from the fixture or from the text of the scenario.</summary>
  private IrModule Imported => scenario.Fixture.Length > 0 && scenario.Source != "Snippet.cs"
    ? PrototypeFixture.Imported(scenario.Fixture)
    : PrototypeFixture.Import("Snippet.cs", File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)));

  /// <summary>The C# the call is read against: the fixture's or the scenario's own.</summary>
  private PrototypeCall Parse(string call) => scenario.Source == "Snippet.cs"
    ? CallText.OfSource(call, File.ReadAllText(scenario.Workspace.Resolve("Snippet.cs")))
    : CallText.OfFixture(call, scenario.Fixture);

  /// <summary>The bytes the call leaves in its array arguments, as an outcome shows them after the result.</summary>
  private static string Unchanged(PrototypeCall call) => PrototypeFixture.Outcome(null, call.Arguments)["void".Length..];

  /// <summary>Translates the source of the scenario to JavaScript into <c>out</c>.</summary>
  [When("^it is translated(?: to JavaScript)?$")]
  public void Translated() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "csharp", "--to", "javascript", "--output", "out");

  /// <summary>Translates the source and runs one call of the generated module in node.</summary>
  [When(@"^it is translated and node calls (F\(.*\))$")]
  public void TranslatedAndCalled(string call) => this.TranslatedAndCalled(this.Parse(call));

  /// <summary>Translates the source and runs one call of the generated module in node, with the arguments rounded to binary32.</summary>
  [When(@"^it is translated and node calls (F\(.*\)) with the arguments rounded to float$")]
  public void TranslatedAndCalledWithFloats(string call) => this.TranslatedAndCalled(this.Parse(call));

  /// <summary>Translates the fixture and runs its method on an array in node.</summary>
  [When(@"^it is translated and node calls (\w+) on (\[.*\])$")]
  public void TranslatedAndCalledOnArray(string method, string array) => this.TranslatedAndCalled(this.Parse($"{method}({array})"));

  private void TranslatedAndCalled(PrototypeCall call) {
    this.Translated();
    Assert.That(scenario.Log.Last.ExitCode, Is.Zero, scenario.Log.Last.Output);
    this._call = call;
    this._outcome = Toolchain.NodeOutcomes(this.Output, this.Module, [call], true).Single();
  }

  /// <summary>The module was written.</summary>
  [Then("^the exit code is 0 and " + StepText.Quoted + " exists$")]
  public void ModuleExists(string path) {
    Assert.That(scenario.Log.Last.ExitCode, Is.Zero, scenario.Log.Last.Output);
    Assert.That(File.Exists(scenario.Workspace.Resolve(path)), Is.True, path);
  }

  /// <summary>A call of the generated module in node has the published result.</summary>
  [Then(@"^node evaluates ""(.+)"" on the generated module to (\d+)$")]
  public void NodeEvaluates(string call, string expected) {
    Assert.That(scenario.Log.Last.ExitCode, Is.Zero, scenario.Log.Last.Output);
    Assert.That(File.Exists(Path.Combine(this.Output, this.Module)), Is.True);
    this._call = this.Parse(call);
    this._outcome = Toolchain.NodeOutcomes(this.Output, this.Module, [this._call], true).Single();
    Assert.That(this._outcome, Is.EqualTo(expected + Unchanged(this._call)));
  }

  /// <summary>The fixture compiled by the C# compiler has the same outcome.</summary>
  [Then(@"^the same call on the fixture compiled by the C# compiler returns (\d+)$")]
  public void CompiledFixtureReturns(string expected)
    => Assert.That(PrototypeFixture.CompiledOutcome(scenario.Fixture, this._call!), Is.EqualTo(expected + Unchanged(this._call!)));

  /// <summary>The interpreter and the compiled fixture have the outcome node had.</summary>
  [Then("^the interpreter and the compiled fixture return the same value$")]
  public void InterpreterAndFixtureAgree() {
    Assert.That(PrototypeFixture.CompiledOutcome(scenario.Fixture, this._call!), Is.EqualTo(this._outcome));
    Assert.That(PrototypeFixture.InterpretedOutcome(PrototypeFixture.Imported(scenario.Fixture), this._call!), Is.EqualTo(this._outcome));
  }

  /// <summary>
  /// The call had a result in node; in the wide feature, whose scenarios name no interpreter step,
  /// the interpreter has it too.
  /// </summary>
  [Then(@"^the result is (-?\d+)n?$")]
  public void ResultIs(string expected) {
    Assert.That(this._outcome, Is.EqualTo(expected));
    Assert.That(PrototypeFixture.InterpretedOutcome(this.Imported, this._call!), Is.EqualTo(expected));
  }

  /// <summary>The interpreter has the same result.</summary>
  [Then(@"^the interpreter returns (-?\d+) for the same call$")]
  public void InterpreterReturns(string expected) => Assert.That(PrototypeFixture.InterpretedOutcome(this.Imported, this._call!), Is.EqualTo(expected));

  /// <summary>The fixture declares the method the scenario names.</summary>
  [Given("^the fixture " + StepText.Quoted + " with " + StepText.Quoted + "$")]
  public void FixtureWith(string path, string declaration) {
    scenario.Workspace.Copy(PrototypeFixture.PathOf(path[..^3]), path);
    scenario.Source = path;
    scenario.Fixture = path[..^3];
    Assert.That(PrototypeFixture.SourceOf(scenario.Fixture), Does.Contain(declaration));
  }

  /// <summary>The array the call was given is as the step says afterwards, and the method returned nothing.</summary>
  [Then(@"^the array is (\[.*\])$")]
  public void ArrayIs(string array) => Assert.That(this._outcome, Is.EqualTo("void | " + array.Replace(" ", "")));

  /// <summary>The call threw a RangeError in node.</summary>
  [Then("^the call throws a RangeError with the message " + StepText.Quoted + "$")]
  public void ThrowsRangeError(string message) => Assert.That(this._outcome, Is.EqualTo("trap: " + message));

  /// <summary>The interpreter traps on the same call.</summary>
  [Then("^the interpreter reports (?:a|an) (divide-by-zero|integer-overflow) trap(?: for the same call)?$")]
  public void InterpreterTraps(string trap) => Assert.That(
    () => IrInterpreter.Call(this.Imported, this._call!.Method, this._call.Arguments),
    Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(trap == "divide-by-zero" ? IrTrap.DivideByZero : IrTrap.IntegerOverflow));

  /// <summary>Takes a C# text that need not be valid.</summary>
  [Given("^the C# text " + StepText.Quoted + "$")]
  public void CSharpText(string text) => scenario.Snippet(StepText.Unescape(text));

  /// <summary>Runs the translate command on the source.</summary>
  [When("^I run the translate command on it$")]
  public void TranslateCommandOnIt() => this.Translated();

  /// <summary>The command was refused with a diagnostic at a line of the source.</summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + @" with line (\d+)$")]
  public void RefusedAtLine(int exitCode, string code, int line) {
    this.Refused(exitCode, code);
    Assert.That(scenario.Log.Last.Output, Does.Contain($"{scenario.Source}({line},"));
  }

  /// <summary>The command was refused with a diagnostic and what it names, and wrote nothing.</summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void RefusedNaming(int exitCode, string code, string subject) {
    this.Refused(exitCode, code);
    Assert.That(scenario.Log.Last.Output, Does.Contain(subject));
  }

  /// <summary>The command was refused with a diagnostic and wrote nothing.</summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + "$")]
  public void Refused(int exitCode, string code) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
    Assert.That(result.Output, Does.Contain(code));

    if (this._table.Length > 0)
      Assert.That(result.Output, Does.Contain($"a public mutable static field {this._table}"));

    Assert.That(Directory.Exists(this.Output), Is.False);
  }

  /// <summary>Nothing but the source is in the workspace.</summary>
  [Then("^(?:the output directory contains no file|nothing is written)$")]
  public void NothingIsWritten() {
    Assert.That(Directory.Exists(this.Output), Is.False);
    Assert.That(Directory.GetFiles(scenario.Workspace.Path, "*", SearchOption.AllDirectories), Is.EqualTo(new[] { scenario.Workspace.Resolve(scenario.Source) }));
  }

  /// <summary>Node rounds to binary32 after every operation, as C# does: the bits are those of the compiled fixture and of the interpreter.</summary>
  [Then("^the bit pattern of the result equals the compiled fixture's$")]
  public void BitsEqualTheCompiledFixtures() {
    Assert.That(PrototypeFixture.SourceOf("wide/OperatorsWide"), Does.Contain("public static float MultiplyAdd(float a, float b) => a * b + a;"));
    var compiled = PrototypeFixture.CompiledOutcome("wide/OperatorsWide", new PrototypeCall("MultiplyAdd", this._call!.Arguments));
    Assert.That(this._outcome, Is.EqualTo(compiled));
    Assert.That(PrototypeFixture.InterpretedOutcome(this.Imported, this._call), Is.EqualTo(compiled));
    Assert.That(File.ReadAllText(Path.Combine(this.Output, this.Module)), Does.Contain("return Math.fround(Math.fround(a * b) + a);"));
  }

  /// <summary>Every call of the fixture has one outcome in node, in the interpreter and in the compiled fixture, and a decode run shows the switch.</summary>
  [Then("^node, the interpreter and the compiled fixture agree on every vector of the fixture$")]
  public void AllAgree() {
    Assert.That(scenario.Log.Last.ExitCode, Is.Zero, scenario.Log.Last.Output);
    Assert.That(PrototypeFixture.SourceOf(scenario.Fixture), Does.Contain("switch (state) {").And.Contain("default:"));
    var calls = PrototypeCases.For(scenario.Fixture);
    var module = PrototypeFixture.Imported(scenario.Fixture);
    var node = Toolchain.NodeOutcomes(this.Output, this.Module, calls, true);
    var mismatches = new List<string>();
    for (var index = 0; index < calls.Count; ++index) {
      var compiled = PrototypeFixture.CompiledOutcome(scenario.Fixture, calls[index]);
      var interpreted = PrototypeFixture.InterpretedOutcome(module, calls[index]);
      if (node[index] != compiled || interpreted != compiled)
        mismatches.Add($"{calls[index]}: C# {compiled}, node {node[index]}, interpreter {interpreted}");
    }

    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
    var decode = new PrototypeCall("Decode", [new byte[] { 0x02, 1, 2, 3, 0x80, 9, 0xFF, 5 }, new byte[8]]);
    Assert.That(Toolchain.NodeOutcomes(this.Output, this.Module, [decode], true).Single(), Is.EqualTo("6 | [2,1,2,3,128,9,255,5] | [1,2,3,9,9,9,0,0]"));
  }

  /// <summary>Takes a class with a constant table and a method that writes to it.</summary>
  [Given(@"^a class with ""public static readonly uint\[\] (\w+) = \{ 1, 2 \};"" and a method assigning \1\[0\]$")]
  public void ClassWritingATable(string table) {
    this._table = table;
    scenario.Snippet($"public static readonly uint[] {table} = {{ 1, 2 }}; public static void F() {{ {table}[0] = 3; }}");
  }

}
