using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/prototype/javascript-to-ir.feature</c>. The expected outcome of a
/// call is never computed here: it is what <c>node</c> does with the original source, and the imported
/// code is held to it in the interpreter, as the JavaScript emitted from the IR and run by <c>node</c>
/// again, and as the C# emitted from the IR and built by the C# compiler.
/// </summary>
[Binding, Scope(Feature = "Import JSDoc-typed JavaScript")]
public sealed class JavaScriptImportSteps(PrototypeScenario scenario) {

  private IrModule? _module;
  private string _export = "";
  private PrototypeCall? _call;
  private string _interpreted = "";
  private int _variants;

  private string Text => File.ReadAllText(scenario.Workspace.Resolve(scenario.Source));

  /// <summary>What node does for the calls; a module that computes in doubles tells -0 from 0.</summary>
  private static IReadOnlyList<string> Exact(string directory, string module, IReadOnlyList<PrototypeCall> calls)
    => Toolchain.NodeOutcomes(directory, module, calls, true);

  /// <summary>
  /// Translates the source to C# and has the C# compiler build the result with overflow checking off
  /// and on. The file is given the name the class is to have.
  /// </summary>
  private IReadOnlyList<GeneratedCSharp> TranslateToCSharp(string className, params string[] further) {
    var folder = "csharp-" + className;
    var source = $"{folder}/{className}{Path.GetExtension(scenario.Source)}";
    scenario.Workspace.Copy(scenario.Workspace.Resolve(scenario.Source), source);
    var result = scenario.Polypiler.Run(["translate", source, "--from", "javascript", "--to", "csharp", "--output", folder + "/out", .. further]);
    Assert.That(result.ExitCode, Is.Zero, result.Output);
    var generated = File.ReadAllText(scenario.Workspace.Resolve($"{folder}/out/{className}.cs"));
    return [GeneratedCSharp.Compile(generated, false), GeneratedCSharp.Compile(generated, true)];
  }

  /// <summary>Copies the pinned file of Cipher's into the workspace.</summary>
  [Given("^the pinned Cipher file " + StepText.Quoted + "$")]
  public void PinnedCipherFile(string name) {
    scenario.Workspace.Copy(Path.Combine(JavaScriptFixture.Directory, name), name);
    scenario.Source = name;
  }

  /// <summary>Imports the one function and translates the file with that export.</summary>
  [When("^I import it with the export " + StepText.Quoted + "$")]
  public void ImportWithExport(string function) {
    this._export = function;
    this._module = JavaScriptFixture.Import(scenario.Source, this.Text, [function]);
    scenario.Polypiler.Run("translate", scenario.Source, "--from", "javascript", "--to", "javascript", "--output", "out", "--export", function);
  }

  /// <summary>The import has the function, and the command wrote the module.</summary>
  [Then("^the import succeeds$")]
  public void ImportSucceeds() {
    Assert.That(this._module!.Functions.Select(function => function.Name), Does.Contain(this._export));
    Assert.That(scenario.Log.Last.ExitCode, Is.Zero, scenario.Log.Last.Output);
  }

  /// <summary>The interpreter has the expected value for the rotation.</summary>
  [Then(@"^the interpreter returns (\d+) for (\w+)\((0x[0-9A-Fa-f]+), (\d+)\)$")]
  public void InterpreterReturns(string expected, string function, string value, int positions) {
    this._call = new PrototypeCall(function, [uint.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture), positions]);
    Assert.That(PrototypeFixture.InterpretedOutcome(this._module!, this._call), Is.EqualTo(expected));
  }

  /// <summary>Node has the value for the original, for the module the command wrote and for the C# translation, built with overflow checking off and on.</summary>
  [Then(@"^node returns (\d+) for the original OpCodes\.(\w+)\((0x[0-9A-Fa-f]+), (\d+)\)$")]
  public void NodeReturns(string expected, string function, string value, int positions) {
    var call = new PrototypeCall(function, [uint.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture), positions]);
    Assert.That(call.ToString(), Is.EqualTo(this._call!.ToString()));
    Assert.That(Exact(scenario.Workspace.Path, scenario.Source, [call]).Single(), Is.EqualTo(expected));
    Assert.That(Exact(scenario.Workspace.Resolve("out"), "OpCodes.mjs", [call]).Single(), Is.EqualTo(expected));
    foreach (var build in this.TranslateToCSharp("OpCodes", "--export", function))
      Assert.That(build.Outcome(call), Is.EqualTo(expected));
  }

  /// <summary>Node has the value for the module the command wrote and for the original.</summary>
  [Then(@"^node evaluates ""(.+)"" on the generated module and on the original to (\d+)$")]
  public void NodeEvaluatesBoth(string text, string expected) {
    var call = CallText.OfScript(text);
    var outcome = expected + PrototypeFixture.Outcome(null, call.Arguments)["void".Length..];
    Assert.That(Exact(scenario.Workspace.Resolve("out"), Path.GetFileName(scenario.Source), [call]).Single(), Is.EqualTo(outcome));
    Assert.That(Exact(scenario.Workspace.Path, scenario.Source, [call]).Single(), Is.EqualTo(outcome));
  }

  /// <summary>Writes the JavaScript of the step into <c>snippet.js</c>.</summary>
  [Given("^the JavaScript " + StepText.Quoted + "$")]
  public void JavaScript(string source) => scenario.Script("snippet.js", StepText.Unescape(source));

  /// <summary>Imports the source and runs one call in the interpreter.</summary>
  [When(@"^it is imported and interpreted with (-?\d+), (-?\d+)$")]
  public void ImportedAndInterpreted(int a, int b) {
    this._call = new PrototypeCall("f", [a, b]);
    this._interpreted = PrototypeFixture.InterpretedOutcome(JavaScriptFixture.Import(scenario.Source, this.Text), this._call);
  }

  /// <summary>
  /// What node does for the call with a source as it is, and with the module translated from it. A
  /// script exports nothing; for node to reach the function, the same text is given an export.
  /// </summary>
  private (string Original, string Translated) OriginalAndTranslated(string source) {
    var name = Path.GetFileNameWithoutExtension(scenario.Source) + ++this._variants;
    scenario.Workspace.Write(name + ".js", source);
    var output = "out-" + name;
    var result = scenario.Polypiler.Run("translate", name + ".js", "--from", "javascript", "--to", "javascript", "--output", output);
    Assert.That(result.ExitCode, Is.Zero, result.Output);
    scenario.Workspace.Write(name + ".cjs", source + $"\nmodule.exports = {{ {this._call!.Method} }};\n");
    return (Exact(scenario.Workspace.Path, name + ".cjs", [this._call]).Single(), Exact(scenario.Workspace.Resolve(output), name + ".mjs", [this._call]).Single());
  }

  /// <summary>The interpreter, node on the original and node on the translated module have the result.</summary>
  [Then(@"^the result is (-?\d+)(?:, as node returns for the original)?$")]
  public void ResultIs(string expected) {
    Assert.That(this._interpreted, Is.EqualTo(expected));
    Assert.That(this.OriginalAndTranslated(this.Text), Is.EqualTo((expected, expected)));
  }

  /// <summary>The import types the result as a double, whatever the JSDoc says.</summary>
  [Then(@"^the result has the type (\w+), whatever ""@returns"" states$")]
  public void ResultHasTheType(string type) {
    Assert.That(this.Text, Does.Contain("@returns {int32}"));
    Assert.That(JavaScriptFixture.Import(scenario.Source, this.Text).Functions.Single().ReturnType.ToString(), Is.EqualTo(type.ToLowerInvariant()));
  }

  /// <summary>The same function with the product written otherwise has the same result everywhere.</summary>
  [Then("^the same function written with " + StepText.Quoted + @" returns (-?\d+) as well$")]
  public void SameFunctionWrittenOtherwise(string product, string expected) {
    var source = this.Text.Replace("(a * b) | 0", product, StringComparison.Ordinal);
    Assert.That(source, Does.Contain(product));
    Assert.That(PrototypeFixture.InterpretedOutcome(JavaScriptFixture.Import(scenario.Source, source), this._call!), Is.EqualTo(expected));
    Assert.That(this.OriginalAndTranslated(source), Is.EqualTo((expected, expected)));
  }

  /// <summary>Translates the source, which imports it.</summary>
  [When("^it is imported$")]
  public void Imported() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "javascript", "--to", "javascript", "--output", "out");

  /// <summary>The import types the parameter and the result.</summary>
  [Then("^the parameter " + StepText.Quoted + @" and the result have the type (\w+)$")]
  public void ParameterAndResult(string parameter, string type) {
    var function = JavaScriptFixture.Import(scenario.Source, this.Text).Functions.Single();
    Assert.That((function.Parameters.Single().Name, function.Parameters.Single().Type.ToString(), function.ReturnType.ToString()),
      Is.EqualTo((parameter, type.ToLowerInvariant(), type.ToLowerInvariant())));
  }

  /// <summary>The command notes the caveat for the function.</summary>
  [Then("^the import notes " + StepText.Quoted + " naming " + StepText.Quoted + "$")]
  public void ImportNotes(string caveat, string function) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.Zero, result.Output);
    Assert.That(result.Output, Does.Contain($"{scenario.Source}: note {caveat}:").And.Contain(": " + function));
  }

  /// <summary>Translates a pinned file with an export.</summary>
  [When("^I import " + StepText.Quoted + " with the export " + StepText.Quoted + "$")]
  public void ImportFileWithExport(string name, string function) {
    this.PinnedCipherFile(name);
    this._export = function;
    scenario.Polypiler.Run("translate", name, "--from", "javascript", "--to", "javascript", "--output", "out", "--export", function);
  }

  /// <summary>The import was refused because the file has no such function, and nothing was written.</summary>
  [Then(@"^the import fails with (PLY\d{4})$")]
  public void ImportFails(string code) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(3), result.Output);
    Assert.That(result.Output, Does.Contain($"error {code}: the file has no function '{this._export}' to export"));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

}
