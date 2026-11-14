using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/lang/cs/be-cs-scopes.feature</c> (#1312): every C# file is built by
/// the C# compiler against the reference assemblies of its profile, with every warning an error, and
/// for this runtime, without and with overflow checking, to be called. What a call has to return is
/// what <c>node</c> returns for the original source or, for IR built here, what the interpreter returns
/// and what <c>node</c> returns for the JavaScript the same IR becomes at ES2022 and at ES5.
/// </summary>
[Binding, Scope(Feature = "Locals of one name in nested C# scopes")]
public sealed class CSharpScopesSteps(PrototypeScenario scenario) {

  private static readonly bool[] _overflowChecking = [false, true];

  private IrModule? _module;
  private string _text = "";

  /// <summary>What a call returns in a generated file, built without and with overflow checking, which must agree.</summary>
  private static string Compiled(string text, PrototypeCall call) {
    var outcomes = _overflowChecking.Select(checking => GeneratedCSharp.Compile(text, checking).Outcome(call)).Distinct().ToList();
    Assert.That(outcomes, Has.Count.EqualTo(1), $"{call} depends on CheckOverflow");
    return outcomes[0];
  }

  /// <summary>The file the last command wrote into <c>out</c>.</summary>
  private string Written() => File.ReadAllText(Directory.GetFiles(scenario.Workspace.Resolve("out")).Single());

  /// <summary>The generated file compiles against the reference assemblies of its profile.</summary>
  [Then(@"^the generated file compiles against the reference assemblies of (\S+) without diagnostics$")]
  public void CompilesForTheProfile(string name) {
    var profile = CSharpProfile.Parse(name);
    this._text = this.Written();
    Assert.That(GeneratedCSharp.ComplaintsFor(this._text, profile.Name, profile.LanguageVersionName), Is.Empty, this._text);
  }

  /// <summary>A call of the generated file returns the value of the step, which node returns for the source.</summary>
  [Then(@"^(\w+)\((-?[0-9.]+)\) returns (-?[0-9.]+), as node returns for the source$")]
  public void ReturnsWhatNodeReturns(string function, string argument, string value) {
    var call = new PrototypeCall(function, [double.Parse(argument, CultureInfo.InvariantCulture)]);
    // node reads the source as the ES module it is.
    scenario.Workspace.Copy(scenario.Workspace.Resolve(scenario.Source), "source.mjs");
    var node = Toolchain.NodeOutcomes(scenario.Workspace.Path, "source.mjs", [call], true).Single();
    Assert.That(node, Is.EqualTo(PrototypeFixture.Show(double.Parse(value, CultureInfo.InvariantCulture))));
    Assert.That(Compiled(this._text, call), Is.EqualTo(node));
  }

  /// <summary>
  /// The IR of the scenario: <c>t: f64 = 0.5</c>, and inside <c>if (n &gt; 0)</c> a <c>t: i32 = n * 2</c>
  /// that the block returns plus one; behind the block <c>t + t</c>.
  /// </summary>
  [Given(@"^the IR function F\(n: i32\): f64 declaring t: f64 = 0\.5 and, inside if \(n > 0\), t: i32 = n \* 2$")]
  public void IrFunction() {
    var n = new IrLocal("n", IrType.I32);
    var outer = new IrLocal("t", IrType.F64);
    var inner = new IrLocal("t", IrType.I32);
    var inside = new IrBlock([
      new IrDeclare(inner, new IrBinary(IrBinaryOp.Multiply, new IrLocalRef(n), new IrConst(IrType.I32, 2))),
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrConvert(IrType.F64, new IrLocalRef(inner)), new IrConst(IrType.F64, BitConverter.DoubleToInt64Bits(1.0))))
    ]);
    var body = new IrBlock([
      new IrDeclare(outer, new IrConst(IrType.F64, BitConverter.DoubleToInt64Bits(0.5))),
      new IrIf(new IrBinary(IrBinaryOp.Greater, new IrLocalRef(n), new IrConst(IrType.I32, 0)), inside, null),
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrLocalRef(outer), new IrLocalRef(outer)))
    ]);
    this._module = new IrModule("Scopes", [new IrFunction("F", true, IrType.F64, [n], [outer, inner], body)]);
    Assert.That(IrVerifier.Verify(this._module), Is.Empty);
  }

  /// <summary>Emits the IR of the scenario as C# for a profile.</summary>
  [When(@"^it is emitted as C# for (\S+)$")]
  public void EmittedFor(string profile) => this._text = CSharpEmitter.Emit(this._module!, CSharpProfile.Parse(profile)).Single().Content;

  /// <summary>The local declared inside the block has the name of the step, and the outer one keeps its own.</summary>
  [Then("^the inner local is written as " + StepText.Quoted + "$")]
  public void InnerLocalIs(string name) => Assert.That(this._text, Does.Contain($"      var {name} = n * 2;\n").And.Contain("    double t = 0.5;\n"), this._text);

  /// <summary>The file compiles for net10.0, and the calls return what the interpreter and node on the ES2022 and ES5 output return.</summary>
  [Then(@"^the file compiles, and F\((-?\d+)\) returns (-?\d+) and F\((-?\d+)\) returns (-?\d+), as the interpreter and node return$")]
  public void CompilesAndReturns(int first, string firstValue, int second, string secondValue) {
    Assert.That(GeneratedCSharp.ComplaintsFor(this._text, "net10.0", "14"), Is.Empty, this._text);
    PrototypeCall[] calls = [new("F", [first]), new("F", [second])];
    string[] expected = [firstValue, secondValue];
    Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(this._module!, call)), Is.EqualTo(expected), "interpreter");
    Assert.That(calls.Select(call => Compiled(this._text, call)), Is.EqualTo(expected), "C#");
    foreach (var profile in new[] { new ProfileCase("es2022", "esm", false, null), new ProfileCase("es5", "cjs", false, null) }) {
      var file = JavaScriptEmitter.Emit(this._module!, profile.Parsed).Single();
      scenario.Workspace.Write(file.Path, file.Content);
      Assert.That(Toolchain.NodeOutcomes(scenario.Workspace.Path, file.Path, calls, true, profile.Mode), Is.EqualTo(expected), $"{profile}:\n{file.Content}");
    }
  }

  /// <summary>The C# fixture of two sibling blocks, from <c>tests/fixtures/prototype/scopes</c>.</summary>
  [Given("^the fixture " + StepText.Quoted + " with two sibling blocks each declaring t, one int and one double$")]
  public void SiblingFixture(string name) {
    scenario.Workspace.Copy(PrototypeFixture.PathOf("scopes/" + Path.GetFileNameWithoutExtension(name)), name);
    scenario.Source = name;
  }

  /// <summary>Translates the fixture to C# for a profile.</summary>
  [When(@"^it is translated to C# for (\S+)$")]
  public void TranslatedFor(string profile) => this._text = scenario.Translate(scenario.Source, "csharp", "csharp", "--profile", profile).Text;

  /// <summary>Two declarations carry the name, none the renamed one; the file compiles and computes what the fixture does.</summary>
  [Then("^the output contains two declarations named " + StepText.Quoted + " and no " + StepText.Quoted + "$")]
  public void TwoDeclarations(string name, string renamed) {
    Assert.That(Regex.Matches(this._text, $@"(?m)^ +(?:var|int|double) {name} = "), Has.Count.EqualTo(2), this._text);
    Assert.That(this._text, Does.Not.Contain(renamed));
    Assert.That(GeneratedCSharp.ComplaintsFor(this._text, "net10.0", "14"), Is.Empty, this._text);
    foreach (var argument in new[] { 3, -1, -3 }) {
      var call = new PrototypeCall("F", [argument]);
      Assert.That(Compiled(this._text, call), Is.EqualTo(PrototypeFixture.CompiledOutcome("scopes/Sibling", call)), call.ToString());
    }
  }

}
