using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Float.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/prototype/float-operations.feature</c>, with the C# compiler and
/// <c>node</c>. The expected bits are the interpreter's, whose portable implementations are correctly
/// rounded, or the MPFR reference vectors; a native function is compared under the bound of ADR 0005.
/// </summary>
[Binding, Scope(Feature = "The complete floating-point operation set")]
public sealed class FloatOperationsSteps(PrototypeScenario scenario) {

  private readonly List<(string Mode, (string Path, string Text) Generated)> _outputs = [];
  private string _to = "";
  private string _profile = "";
  private int _bound;
  private (string Report, List<string> Exact, List<string> Bounded, int Special, int Finite) _native;

  private string Directory => scenario.Workspace.Path;

  private IReadOnlyList<string> Run(string to, string profile, (string Path, string Text) generated, IReadOnlyList<PrototypeCall> calls)
    => to == "csharp" ? FloatOperationsTests.CSharpOutcomes(generated.Text, calls) : FloatOperationsTests.JavaScriptOutcomes(this.Directory, generated.Path, profile, calls);

  /// <summary>Translates the C# source to a target with the options of the step.</summary>
  [When("^it is translated to (csharp|javascript) with " + StepText.Quoted + "$")]
  public void TranslatedTo(string to, string options) {
    var arguments = CommandLine.Split(options).ToArray();
    this._to = to;
    this._profile = arguments[Array.IndexOf(arguments, "--profile") + 1];
    this._outputs.Add(("strict", scenario.Translate(scenario.Source, "csharp", to, arguments)));
  }

  /// <summary>The DCT of the samples, in both precisions, has the interpreter's bits, and the file says it is strict.</summary>
  [Then("^Transform of the samples 1, 2, 3, 4, 5, 6, 7, 8 returns the eight bit patterns of the interpreter$")]
  public void TransformHasTheInterpretersBits() {
    var generated = this._outputs.Single().Generated;
    double[] samples = [1, 2, 3, 4, 5, 6, 7, 8];
    PrototypeCall[] calls = [
      new("Transform", [samples, new double[8]]),
      new("TransformSingle", [samples.Select(sample => (float)sample).ToArray(), new float[8]])
    ];
    var expected = FloatOperationsTests.Interpreted(PrototypeFixture.Imported(scenario.Fixture), calls);
    Assert.That(this.Run(this._to, this._profile, generated, calls), Is.EqualTo(expected), generated.Text[..200]);
    Assert.That(generated.Text, Does.Contain("Floating point: strict"));
  }

  /// <summary>Translates the C# source with the options of the step, which name the target.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    this._to = arguments[arguments.IndexOf("--to") + 1];
    this._profile = arguments[arguments.IndexOf("--profile") + 1];
    arguments.RemoveRange(arguments.IndexOf("--to"), 2);
    this._outputs.Add(("accurate", scenario.Translate(scenario.Source, "csharp", this._to, [.. arguments])));
  }

  /// <summary>The output calls the native function, and its header logs the bound of ADR 0005.</summary>
  [Then("^the output calls " + StepText.Quoted + " and its header names " + StepText.Quoted + " with the bound of ADR 0005$")]
  public void CallsTheNativeFunction(string call, string name) {
    var op = Enum.Parse<IrIntrinsicOp>(name);
    this._bound = NativeAccuracy.Bound(op, NativeAccuracy.Slack(NativeAccuracy.Node, op, IrType.F64)!.Value);
    var text = this._outputs.Single().Generated.Text;
    Assert.That(text, Does.Contain($"return {call}(x);").And.Contain($"//   {name} of f64: ulp(1), slack 1 on node, bound {this._bound}"));
  }

  /// <summary>Every finite result of the native function on the reference vectors is within the bound of the correctly rounded one.</summary>
  [Then("^for every reference vector the result is within that bound of the correctly rounded value$")]
  public void WithinTheBound() {
    var generated = this._outputs.Single().Generated;
    this._native = FloatOperationsTests.CheckNative("sin", IrType.F64, this._bound, calls => FloatOperationsTests.JavaScriptOutcomes(this.Directory, generated.Path, this._profile, calls));
    TestContext.Out.WriteLine($"Math.sin of node against sin.f64: {this._native.Report}");
    Assert.That(this._native.Finite, Is.GreaterThan(0));
    Assert.That(this._native.Bounded, Is.Empty, string.Join("; ", this._native.Bounded.Take(10)));
  }

  /// <summary>Every zero, NaN and infinity among the results is exactly the correct one.</summary>
  [Then("^the sign of every zero result and every NaN and infinity matches exactly$")]
  public void SpecialResultsAreExact() {
    Assert.That(this._native.Special, Is.GreaterThan(0));
    Assert.That(this._native.Exact, Is.Empty, string.Join("; ", this._native.Exact.Take(10)));
  }

  /// <summary>The special values of the step in the interpreter and in every profile of both backends in both modes.</summary>
  [Then(@"^Atan2\(-0\.0, -1\.0\) returns -π rounded, Atan2\(-0\.0, 1\.0\) returns -0\.0, Hypot\(∞, NaN\) returns ∞, Pow\(NaN, 0\) returns 1, Log\(-1\) returns a NaN and Sqrt\(-0\.0\) returns -0\.0 in the interpreter and in every profile of both backends in both modes$")]
  public void SpecialValuesAreExactEverywhere() {
    var source = scenario.Snippet(
      "public static double A(double y, double x) => System.Math.Atan2(y, x); "
      + "public static double H(double x, double y) => System.Math.Pow(x, y) + System.Double.Hypot(x, y); "
      + "public static double P(double x, double y) => System.Math.Pow(x, y); "
      + "public static double L(double x) => System.Math.Log(x); "
      + "public static double S(double x) => System.Math.Sqrt(x); "
      + "public static double Y(double x, double y) => System.Double.Hypot(x, y);");
    PrototypeCall[] calls = [
      new("A", [-0.0, -1.0]), new("A", [-0.0, 1.0]), new("Y", [double.PositiveInfinity, double.NaN]),
      new("P", [double.NaN, 0.0]), new("L", [-1.0]), new("S", [-0.0])
    ];
    var expected = new[] { PrototypeFixture.Show(-Math.PI), PrototypeFixture.Show(-0.0), PrototypeFixture.Show(double.PositiveInfinity), "1", "NaN", PrototypeFixture.Show(-0.0) };
    Assert.That(FloatOperationsTests.Interpreted(PrototypeFixture.Import("Snippet.cs", File.ReadAllText(scenario.Workspace.Resolve(source))), calls), Is.EqualTo(expected));
    var targets = (from mode in FloatOperationsTests.Modes from target in FloatOperationsTests.Targets() select (Mode: mode, target.To, target.Profile)).ToList();
    Sweep.Each(targets, target => {
      var generated = scenario.Translate(source, "csharp", target.To, "--profile", target.Profile, "--float", target.Mode);
      Assert.That(this.Run(target.To, target.Profile, generated, calls), Is.EqualTo(expected), $"{target.Profile} {target.Mode}");
    });
  }

  /// <summary>Writes a JavaScript function into a module of the scenario, its two parameters and its result declared as numbers.</summary>
  [Given("^the JavaScript function " + StepText.Quoted + " with both parameters as numbers$")]
  public void JavaScriptFunctionOfNumbers(string function)
    => scenario.Script("pow.mjs", "/** @param {number} x @param {number} y @returns {number} */\n" + StepText.Unescape(function) + "\n");

  /// <summary>Writes a JavaScript function into a module of the scenario, named after what it calls.</summary>
  [Given("^the JavaScript function " + StepText.Quoted + "$")]
  public void JavaScriptFunction(string function) {
    var text = StepText.Unescape(function);
    scenario.Script(text.Contains("Math.random", StringComparison.Ordinal) ? "random.mjs" : "round.mjs", text + "\n");
  }

  /// <summary>Translates the JavaScript source to C# in both modes.</summary>
  [When("^it is translated to C#$")]
  public void TranslatedToCSharp() {
    foreach (var mode in FloatOperationsTests.Modes)
      this._outputs.Add((mode, scenario.Translate(scenario.Source, "javascript", "csharp", "--float", mode)));
  }

  /// <summary>Node's own answers for the power's special cases, which C#'s Math.Pow does not give, and the C# translation gives them in both modes.</summary>
  [Then(@"^f\(1, NaN\) and f\(-1, Infinity\) return NaN, as in node, although Math.Pow returns 1$")]
  public void PowerKeepsJavaScriptsSpecialCases() {
    PrototypeCall[] calls = [new("f", [1.0, double.NaN]), new("f", [-1.0, double.PositiveInfinity]), new("f", [2.0, 10.0])];
    var node = Toolchain.NodeOutcomes(this.Directory, scenario.Source, calls, true);
    Assert.That(node, Is.EqualTo(new[] { "NaN", "NaN", "1024" }));
    Assert.That(Math.Pow(1.0, double.NaN), Is.EqualTo(1.0), "C#'s Math.Pow is IEEE 754's");
    Assert.That(this._outputs, Has.Count.EqualTo(2));
    foreach (var (mode, generated) in this._outputs)
      Assert.That(FloatOperationsTests.CSharpOutcomes(generated.Text, calls), Is.EqualTo(node), mode);
  }

  /// <summary>The strict binary32 exponential of every profile of both backends is the correctly rounded one on every vector.</summary>
  [Then("^in strict mode every profile of both backends returns the bits of the correctly rounded binary32 result for every reference vector the helper's contract covers$")]
  public void SinglePrecisionIsCorrectlyRounded() {
    var vectors = FloatVectors.Of("exp", IrType.F32);
    var calls = vectors.Select(vector => new PrototypeCall("F", [(float)FloatVectors.Value(IrType.F32, vector.Operands[0])])).ToList();
    var expected = vectors.Select(vector => PrototypeFixture.Show(FloatVectors.Value(IrType.F32, vector.Result))).ToList();
    Assert.That(calls, Is.Not.Empty);
    Sweep.Each(FloatOperationsTests.Targets().ToList(), target => {
      var generated = scenario.Translate(scenario.Source, "csharp", target.To, "--profile", target.Profile, "--float", "strict");
      Assert.That(this.Run(target.To, target.Profile, generated, calls), Is.EqualTo(expected), target.Profile);
    });
  }

  /// <summary>A tie rounds up in node, in the interpreter and in every C# profile.</summary>
  [Then(@"^f\(-2\.5\) returns -2 and f\(2\.5\) returns 3 in node, in the interpreter and in every C# profile$")]
  public void TieRoundsUp() {
    PrototypeCall[] calls = [new("f", [-2.5]), new("f", [2.5])];
    string[] expected = ["-2", "3"];
    Assert.That(Toolchain.NodeOutcomes(this.Directory, scenario.Source, calls, true), Is.EqualTo(expected));
    var module = JavaScriptFixture.Import(scenario.Source, File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)));
    Assert.That(FloatOperationsTests.Interpreted(module, calls), Is.EqualTo(expected));
    Sweep.Each([.. CSharpProfile.All.Select(profile => profile.Name)], profile => {
      var generated = scenario.Translate(scenario.Source, "javascript", "csharp", "--profile", profile);
      Assert.That(FloatOperationsTests.CSharpOutcomes(generated.Text, calls), Is.EqualTo(expected), profile);
    });
  }

  /// <summary>Runs the translate command on the JavaScript source, to C#.</summary>
  [When("^I run the translate command$")]
  public void TranslateCommand() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "javascript", "--to", "csharp", "--output", "out");

  /// <summary>The command was refused with a diagnostic naming the construct, and wrote nothing.</summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Refused(int exitCode, string code, string subject) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
    Assert.That(result.Output, Does.Contain(code).And.Contain(subject));
    Assert.That(System.IO.Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

}
