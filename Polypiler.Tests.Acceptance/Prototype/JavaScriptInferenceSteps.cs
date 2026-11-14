using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/prototype/javascript-inference.feature</c>. An expected outcome is
/// what <c>node</c> does with the original source; the interpreter and the module emitted from the IR
/// are held to it, a double by its bits.
/// </summary>
[Binding, Scope(Feature = "Identify data types of untyped JavaScript")]
public sealed class JavaScriptInferenceSteps(PrototypeScenario scenario) {

  private string _function = "";
  private IReadOnlyList<string> _everywhere = [];

  private string Text => File.ReadAllText(scenario.Workspace.Resolve(scenario.Source));

  private IrFunction Function => JavaScriptFixture.Import(scenario.Source, this.Text).Functions.Single();

  /// <summary>Translates the source back to JavaScript into <c>out</c>.</summary>
  private CommandResult Translate(params string[] further)
    => scenario.Polypiler.Run(["translate", scenario.Source, "--from", "javascript", "--to", "javascript", "--output", "out", .. further]);

  /// <summary>What a call does in node on the original, in the interpreter and in node on the module the command writes.</summary>
  private IReadOnlyList<string> Everywhere(PrototypeCall call) {
    var result = this.Translate();
    Assert.That(result.ExitCode, Is.Zero, result.Output);
    var name = Path.GetFileName(scenario.Source);
    return [
      Toolchain.NodeOutcomes(scenario.Workspace.Path, scenario.Source, [call], true).Single(),
      PrototypeFixture.InterpretedOutcome(JavaScriptFixture.Import(name, this.Text), call),
      Toolchain.NodeOutcomes(scenario.Workspace.Resolve("out"), name, [call], true).Single()
    ];
  }

  /// <summary>Copies an untyped fixture, which has no JSDoc.</summary>
  [Given("^the fixture " + StepText.Quoted + " which contains no JSDoc$")]
  public void UntypedFixture(string path) {
    var name = path["js/".Length..^".mjs".Length];
    Assert.That(JavaScriptFixture.SourceOf(name), Does.Not.Contain("/**").And.Not.Contain("@param"));
    scenario.Workspace.Copy(JavaScriptFixture.PathOf(name), Path.GetFileName(path));
    scenario.Source = Path.GetFileName(path);
  }

  /// <summary>Translates the fixture with the explanations of the types.</summary>
  [When("^it is imported and " + StepText.Quoted + " is explained$")]
  public void ImportedAndExplained(string function) {
    this._function = function;
    var result = this.Translate("--explain-types");
    Assert.That(result.ExitCode, Is.Zero, result.Output);
  }

  /// <summary>The explanation and the import give the local the type.</summary>
  [Then("^the local " + StepText.Quoted + @" has the type (\w+)$")]
  public void LocalHasTheType(string local, string type) {
    Assert.That(scenario.Log.Last.Output, Does.Contain($"{scenario.Source}: type {this._function}: local {local}: {type.ToLowerInvariant()} from '"));
    var explained = JavaScriptFrontend.Import(scenario.Source, this.Text).Explanations.Single(explanation => (explanation.Function, explanation.Role, explanation.Name) == (this._function, "local", local));
    Assert.That(explained.Type.ToString(), Is.EqualTo(type.ToLowerInvariant()));
  }

  /// <summary>Node on the original, the interpreter and node on the module the command wrote have the published value.</summary>
  [Then(@"^node on the original, the interpreter and node on the re-emitted module return (\d+) for (.+)$")]
  public void AllReturn(string expected, string text) {
    var call = CallText.OfScript(text);
    var outcome = expected + PrototypeFixture.Outcome(null, call.Arguments)["void".Length..];
    Assert.That(Toolchain.NodeOutcomes(scenario.Workspace.Path, scenario.Source, [call], true).Single(), Is.EqualTo(outcome));
    Assert.That(PrototypeFixture.InterpretedOutcome(JavaScriptFrontend.Import(scenario.Source, this.Text).Module!, call), Is.EqualTo(outcome));
    Assert.That(Toolchain.NodeOutcomes(scenario.Workspace.Resolve("out"), scenario.Source, [call], true).Single(), Is.EqualTo(outcome));
  }

  /// <summary>Writes the JavaScript of the step into <c>snippet.mjs</c>.</summary>
  [Given("^the JavaScript " + StepText.Quoted + "$")]
  public void JavaScript(string source) => scenario.Script("snippet.mjs", StepText.Unescape(source));

  /// <summary>Writes a switch whose first section falls through into the statements of the second.</summary>
  [Given("^a JavaScript switch whose first section has statements and no break$")]
  public void FallingSwitch()
    => scenario.Script("snippet.mjs", "export function f(n) {\n  let x = 0;\n  switch (n | 0) {\n    case 1:\n      x = 1;\n    case 2:\n      x = 2;\n      break;\n  }\n  return x;\n}");

  /// <summary>Translates the source, which imports it.</summary>
  [When("^it is imported$")]
  public void Imported() => this.Translate();

  /// <summary>The import gives the parameters and the result the types of the step.</summary>
  [Then(@"^the parameters a and b have the type (\w+) and the result (\w+)$")]
  public void ParametersAndResult(string parameters, string result) {
    var function = this.Function;
    Assert.That(function.Parameters.Select(parameter => (parameter.Name, parameter.Type.ToString())), Is.EqualTo(new[] { ("a", parameters.ToLowerInvariant()), ("b", parameters.ToLowerInvariant()) }));
    Assert.That(function.ReturnType.ToString(), Is.EqualTo(result.ToLowerInvariant()));
  }

  /// <summary>The command notes the caveat for the function, and the function computes the same everywhere.</summary>
  [Then("^the module carries the caveat " + StepText.Quoted + " naming a and b$")]
  public void CarriesTheCaveat(string caveat) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.Zero, result.Output);
    Assert.That(result.Output, Does.Contain($"{scenario.Source}: note {caveat}:").And.Contain(": f(a, b)"));
    Assert.That(this.Everywhere(new PrototypeCall("f", [-1, 1])), Is.EqualTo(new[] { "4294967294", "4294967294", "4294967294" }));
  }

  /// <summary>Runs one call of two doubles everywhere.</summary>
  [When(@"^it is imported and interpreted with (\S+) and (\S+)$")]
  public void ImportedAndInterpreted(string a, string b) {
    var call = CallText.OfScript($"f({a}, {b})");
    this._everywhere = this.Everywhere(call);
  }

  /// <summary>The import keeps the parameters and the result doubles.</summary>
  [Then("^the parameters and the result have the type F64$")]
  public void AllDoubles() {
    var function = this.Function;
    Assert.That(function.Parameters.Select(parameter => parameter.Type).Append(function.ReturnType), Has.All.EqualTo(IrType.F64));
  }

  /// <summary>The result has the bits of the step everywhere.</summary>
  [Then("^the result has the bit pattern 0x([0-9A-Fa-f]{16})$")]
  public void ResultHasTheBits(string bits) {
    Assert.That(this._everywhere, Has.All.EqualTo("f:" + bits.ToLowerInvariant()));
    var result = (double)IrInterpreter.Call(JavaScriptFixture.Import(scenario.Source, this.Text), "f", [0.1, 0.2])!;
    Assert.That(BitConverter.DoubleToInt64Bits(result), Is.EqualTo(Convert.ToInt64(bits, 16)));
  }

  /// <summary>Node on the original, the interpreter and node on the re-emitted module have the result.</summary>
  [Then(@"^the result is (-?\d+)(?:, as node returns for the original)?$")]
  public void ResultIs(string expected) => Assert.That(this._everywhere, Has.Count.EqualTo(3).And.All.EqualTo(expected));

  /// <summary>The product is computed in doubles and only then converted to a 32-bit integer.</summary>
  [Then("^the IR multiplies in F64 before converting$")]
  public void MultipliesInDoubles() {
    var returned = (IrIntrinsic)((IrReturn)this.Function.Body.Statements.Single()).Value!;
    Assert.That(returned.Op, Is.EqualTo(IrIntrinsicOp.ToInt32));
    Assert.That(returned.Arguments.Single(), Is.InstanceOf<IrBinary>().With.Property(nameof(IrBinary.Op)).EqualTo(IrBinaryOp.Multiply).And.Property(nameof(IrExpr.Type)).EqualTo(IrType.F64));
  }

  /// <summary>The import was refused with the code, and nothing was written.</summary>
  [Then(@"^the import fails with (PLY\d{4})$")]
  public void ImportFails(string code) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(3), result.Output);
    Assert.That(result.Output, Does.Contain($"error {code}: "));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

  /// <summary>The import was refused with the code, naming the variable.</summary>
  [Then(@"^the import fails with (PLY\d{4}) naming " + StepText.Quoted + "$")]
  public void ImportFailsNaming(string code, string name) {
    this.ImportFails(code);
    Assert.That(scenario.Log.Last.Output, Does.Contain($"error {code}: '{name}' "));
  }

  /// <summary>The import was refused with the code at the section that falls through.</summary>
  [Then(@"^the import fails with (PLY\d{4}) at that section$")]
  public void ImportFailsAtTheSection(string code) {
    this.ImportFails(code);
    var lines = this.Text.Split('\n');
    var line = Array.FindIndex(lines, candidate => candidate.TrimStart().StartsWith("case 1:", StringComparison.Ordinal)) + 1;
    var column = lines[line - 1].IndexOf("case", StringComparison.Ordinal) + 1;
    Assert.That(scenario.Log.Last.Output, Does.Contain($"{scenario.Source}({line},{column}): error {code}: 'a switch section that falls through into the next' is not supported yet"));
  }

  /// <summary>The same function with its result bounded to 64 bits and its parameter declared imports as a 64-bit integer, which computes the same everywhere.</summary>
  [Then("^" + StepText.Quoted + " with JSDoc " + StepText.Quoted + " imports with the result type (\\w+)$")]
  public void BoundedImports(string function, string jsdoc, string type) {
    scenario.Script("snippet.mjs", $"/** {jsdoc} */ {function}");
    Assert.That(this.Function.ReturnType.ToString(), Is.EqualTo(type.ToLowerInvariant()));
    Assert.That(this.Everywhere(new PrototypeCall("f", [14695981039346656037UL])), Has.All.EqualTo("12638153115695167455"));
  }

}
