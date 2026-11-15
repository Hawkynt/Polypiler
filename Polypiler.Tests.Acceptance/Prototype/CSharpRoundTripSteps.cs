using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/prototype/ir-to-csharp.feature</c> and <c>ir-to-csharp-wide.feature</c>.
/// The file the command writes is built by Roslyn once with the project's overflow checking off and
/// once with it on, then loaded and called. The expected outcome of a call is never computed here: it
/// is what the original fixture does after the C# compiler built it into this assembly.
/// </summary>
[Binding]
[Scope(Feature = "Emit the prototype IR as C#")]
[Scope(Feature = "Emit the widened IR as C#")]
public sealed class CSharpRoundTripSteps(PrototypeScenario scenario) {

  private static readonly bool[] _overflowChecking = [false, true];

  private readonly List<GeneratedCSharp> _builds = [];
  private IrModule? _module;
  private string _emitted = "";
  private IReadOnlyList<string> _problems = [];

  /// <summary>The one file the last command line wrote into <c>out</c>.</summary>
  private string Generated() {
    Assert.That(scenario.Log.Last.ExitCode, Is.Zero, scenario.Log.Last.Output);
    return File.ReadAllText(Directory.GetFiles(scenario.Workspace.Resolve("out")).Single(path => Path.GetFileName(path) != "polypiler.manifest.json"));
  }

  /// <summary>The C# was written where the step says.</summary>
  [Then("^the exit code is 0 and " + StepText.Quoted + " exists$")]
  public void FileExists(string path) {
    Assert.That(scenario.Log.Last.ExitCode, Is.Zero, scenario.Log.Last.Output);
    Assert.That(File.Exists(scenario.Workspace.Resolve(path)), Is.True, path);
  }

  /// <summary>The generated file compiles without a warning with overflow checking off and on.</summary>
  [Then("^the generated file compiles (?:with the C# compiler )?without warnings in both checked and unchecked project modes$")]
  public void CompilesInBothModes() {
    var generated = this.Generated();
    this._builds.AddRange(_overflowChecking.Select(checking => GeneratedCSharp.Compile(generated, checking)));
  }

  /// <summary>Each build has the published outcome, which is also the original fixture's.</summary>
  [Then(@"^calling ""(.+)"" on the compiled result returns (\d+)$")]
  public void CallingReturns(string text, string expected) {
    var call = CallText.OfFixture(text, scenario.Fixture);
    var whole = expected + PrototypeFixture.Outcome(null, call.Arguments)["void".Length..];
    foreach (var (build, checking) in this._builds.Zip(_overflowChecking))
      Assert.That(build.Outcome(call), Is.EqualTo(whole), $"CheckOverflow {checking}");

    Assert.That(PrototypeFixture.CompiledOutcome(scenario.Fixture, call), Is.EqualTo(whole));
  }

  /// <summary>The generated C# of a JavaScript fixture compiles in both modes, and each build computes the checksum.</summary>
  [Then(@"^the generated C# compiles and (\w+\(.*\)) returns (\d+)$")]
  public void GeneratedCompilesAndReturns(string text, string expected) {
    var generated = this.Generated();
    foreach (var checking in _overflowChecking) {
      var build = GeneratedCSharp.Compile(generated, checking);
      var call = CallText.Parse(text, build.Method(text[..text.IndexOf('(')]));
      Assert.That(build.Outcome(call), Is.EqualTo(expected + PrototypeFixture.Outcome(null, call.Arguments)["void".Length..]), $"CheckOverflow {checking}");
    }
  }

  /// <summary>Translates the source to C# into <c>out</c>.</summary>
  [When("^it is translated to C#$")]
  public void TranslatedToCSharp() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "csharp", "--to", "csharp", "--output", "out");

  /// <summary>Translates the source to C# and compiles it with overflow checking off and on.</summary>
  [When("^it is translated to C# and compiled$")]
  public void TranslatedAndCompiled() {
    this.TranslatedToCSharp();
    this.CompilesInBothModes();
  }

  /// <summary>Translates the source to C# and compiles it with the project's overflow checking as the step says.</summary>
  [When("^it is translated to C# and compiled with CheckOverflow (enabled|disabled)$")]
  public void TranslatedAndCompiledWith(string checking) {
    this.TranslatedToCSharp();
    this._builds.Add(GeneratedCSharp.Compile(this.Generated(), checking == "enabled"));
  }

  /// <summary>Every build has the outcome of the step and throws nothing.</summary>
  [Then(@"^(F\(.*\)) returns (-?\d+)(?: and throws nothing)?$")]
  public void Returns(string text, string expected) {
    var call = CallText.OfSource(text, File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)));
    Assert.That(this._builds, Is.Not.Empty);
    foreach (var build in this._builds)
      Assert.That(build.Outcome(call), Is.EqualTo(expected));
  }

  /// <summary>Every build throws the .NET exception of the step.</summary>
  [Then(@"^(F\(.*\)) throws (\w+Exception)$")]
  public void Throws(string text, string exception) {
    var call = CallText.OfSource(text, File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)));
    Assert.That(this._builds, Is.Not.Empty);
    foreach (var build in this._builds) {
      var thrown = Assert.Throws<TargetInvocationException>(() => build.Method("F").Invoke(null, call.FreshArguments()))!;
      Assert.That(thrown.InnerException?.GetType().FullName, Is.EqualTo("System." + exception));
    }
  }

  /// <summary>Every build computes the bits the original fixture computes.</summary>
  [Then(@"^the bit pattern of (F\(.*\)) equals the original fixture's$")]
  public void BitsEqualTheOriginal(string text) {
    var call = CallText.OfSource(text, File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)));
    Assert.That(PrototypeFixture.SourceOf("wide/OperatorsWide"), Does.Contain("public static float MultiplyAdd(float a, float b) => a * b + a;"));
    var original = (float)PrototypeFixture.Compiled("wide/OperatorsWide", "MultiplyAdd").Invoke(null, call.FreshArguments())!;
    foreach (var build in this._builds) {
      var result = (float)build.Method("F").Invoke(null, call.FreshArguments())!;
      Assert.That(BitConverter.SingleToInt32Bits(result), Is.EqualTo(BitConverter.SingleToInt32Bits(original)));
    }
  }

  /// <summary>Builds a function whose local is named after a C# keyword.</summary>
  [Given("^an IR function with a local named " + StepText.Quoted + "$")]
  public void FunctionWithALocalNamed(string name) {
    var parameter = new IrLocal("a", IrType.I32);
    var local = new IrLocal(name, IrType.I32);
    this._module = new IrModule("Keywords", [new IrFunction("F", true, IrType.I32, [parameter], [local], new IrBlock([
      new IrDeclare(local, new IrBinary(IrBinaryOp.Add, new IrLocalRef(parameter), new IrConst(IrType.I32, 1))),
      new IrReturn(new IrLocalRef(local))
    ]))]);
  }

  /// <summary>Builds a module that adds an <c>I32</c> to a <c>Bool</c>.</summary>
  [Given("^a hand-built IR module that adds an I32 to a Bool$")]
  public void IllTypedModule() => this._module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [], [], new IrBlock([
    new IrReturn(new IrBinary(IrBinaryOp.Add, new IrConst(IrType.I32, 1), new IrConst(IrType.Bool, 1)))
  ]))]);

  private sealed record Riddle : IrStmt;

  /// <summary>Builds a module with a statement the C# backend does not know.</summary>
  [Given("^an IR module containing a node kind unknown to the C# backend$")]
  public void ModuleWithAnUnknownNode() => this._module = new IrModule("M", [new IrFunction("F", true, IrType.Void, [], [], new IrBlock([new Riddle()]))]);

  /// <summary>Checks the module and, where the check finds nothing, emits it.</summary>
  [When("^it is (?:emitted as C#|checked)$")]
  public void Emitted() {
    this._problems = [.. CSharpEmitter.Check(this._module!).Select(problem => problem.Code)];
    if (this._problems.Count == 0)
      this._emitted = CSharpEmitter.Emit(this._module!).Single().Content;
  }

  /// <summary>The output escapes the keyword, compiles in both modes and computes what the interpreter computes.</summary>
  [Then("^the output declares " + StepText.Quoted + " and compiles$")]
  public void DeclaresAndCompiles(string name) {
    Assert.That(this._emitted, Does.Contain($"var {name} = a + 1;"));
    foreach (var checking in _overflowChecking)
      Assert.That(GeneratedCSharp.Compile(this._emitted, checking).Method("F").Invoke(null, [41]), Is.EqualTo(42).And.EqualTo(IrInterpreter.Call(this._module!, "F", [41])));
  }

  /// <summary>The check reports the code, and emitting the module is refused.</summary>
  [Then(@"^(?:the emitter returns|the result is) (PLY\d{4}) and (?:no file|nothing is emitted)$")]
  public void Refused(string code) {
    Assert.That(this._problems, Is.EqualTo(new[] { code }));
    Assert.That(() => CSharpEmitter.Emit(this._module!), NUnit.Framework.Throws.ArgumentException);
  }

  /// <summary>Translates a class of one method with the options of the step.</summary>
  [When("^I run the translate command with " + StepText.Quoted + "$")]
  public void TranslateCommandWith(string options) {
    scenario.Snippet("public static int F() => 1;");
    scenario.Polypiler.Run(["translate", "Snippet.cs", "--from", "csharp", .. CommandLine.Split(options), "--output", "out"]);
  }

  /// <summary>The first line of code is the namespace declaration, and the class compiles in it.</summary>
  [Then("^the generated file starts its code with " + StepText.Quoted + "$")]
  public void StartsWith(string line) {
    var text = this.Generated();
    Assert.That(text.Split('\n').First(candidate => candidate.Length > 0 && !candidate.StartsWith("//", StringComparison.Ordinal)), Is.EqualTo(line));
    Assert.That(GeneratedCSharp.Compile(text, false).ClassName, Is.EqualTo(line["namespace ".Length..^1] + ".Snippet"));
  }

  /// <summary>The constant table is one read-only field with every element.</summary>
  [Then(@"^the output declares exactly one ""private static readonly uint\[\]"" field with (\d+) elements$")]
  public void DeclaresOneTable(int elements) {
    var declarations = Regex.Matches(this.Generated(), @"private static readonly uint\[\] (\w+) = \{([^}]*)\};");
    Assert.That(declarations, Has.Count.EqualTo(1));
    Assert.That(declarations[0].Groups[2].Value.Split(','), Has.Length.EqualTo(elements));
  }

  /// <summary>No statement writes to an element of the table.</summary>
  [Then("^no method assigns to an element of it$")]
  public void NoMethodAssigns() {
    var text = this.Generated();
    var name = Regex.Match(text, @"private static readonly uint\[\] (\w+) =").Groups[1].Value;
    Assert.That(Regex.IsMatch(text, Regex.Escape(name) + @"\[[^\]]*\]\s*(=[^=]|\+\+|--)"), Is.False);
  }

}
