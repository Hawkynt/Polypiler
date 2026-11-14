using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The scenarios of <c>tests/features/prototype/float-operations.feature</c> and the end-to-end rows
/// of the test design of #1301, with the real <c>polypiler</c> executable, the C# compiler and
/// <c>node</c>. The expected bits are the interpreter's, whose portable implementations are correctly
/// rounded, or the MPFR reference vectors; a native function is compared under the bound of ADR 0005.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class FloatOperationsTests {

  private static readonly string[] _scenarios = [
    "In strict mode every target returns the same bits",
    "In accurate mode a native sine is used and its difference is bounded and logged",
    "Special values are exact everywhere",
    "JavaScript's power keeps its own special cases through C#",
    "Single precision is not double precision rounded",
    "JavaScript rounding of a tie goes up",
    "Random numbers are refused"
  ];

  private static readonly string[] _modes = ["strict", "accurate"];
  private static readonly string[] _levels = ["es5", "es2015", "es2017", "es2020", "es2022"];

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(this._directory, "float"));
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Ensures the feature and its NUnit bindings stay in agreement.</summary>
  [Test]
  public void FeatureScenarios_HaveBindings() {
    var feature = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/features/prototype/float-operations.feature"));
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario(?: Outline)?: (.+?)\r?$").Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
  }

  // ---------------------------------------------------------------- translating

  /// <summary>Translates a file of the working directory with the real executable and returns the path of the generated file and its text.</summary>
  private (string Path, string Text) Translate(string source, string from, string to, params string[] further)
    => this.Translate(false, source, from, to, further);

  /// <summary>Translates a file of the working directory in process or with the real executable and returns the path of the generated file and its text.</summary>
  private (string Path, string Text) Translate(bool inProcess, string source, string from, string to, params string[] further) {
    var output = Path.Combine(this._directory, "out-" + Guid.NewGuid().ToString("N"));
    var (exitCode, text) = Toolchain.Polypiler(inProcess, this._directory, ["translate", source, "--from", from, "--to", to, "--output", output, .. further]);
    Assert.That(exitCode, Is.Zero, text);
    var file = Directory.GetFiles(output).Single();
    return (file, File.ReadAllText(file));
  }

  private string Snippet(string methods) {
    File.WriteAllText(Path.Combine(this._directory, "Snippet.cs"), PrototypeFixture.Wrap(methods));
    return "Snippet.cs";
  }

  private string Script(string name, string text) {
    File.WriteAllText(Path.Combine(this._directory, name), text);
    return name;
  }

  private string Fixture(string fixture) {
    var target = Path.Combine(this._directory, fixture.Replace('/', Path.DirectorySeparatorChar) + ".cs");
    File.Copy(PrototypeFixture.PathOf(fixture), target, true);
    return fixture + ".cs";
  }

  /// <summary>The outcomes of calls of a generated C# file, compiled for the running runtime.</summary>
  private static IReadOnlyList<string> CSharpOutcomes(string text, IReadOnlyList<PrototypeCall> calls) {
    var build = GeneratedCSharp.Compile(text, false);
    return [.. calls.Select(build.Outcome)];
  }

  /// <summary>The outcomes of calls of a generated JavaScript file in node.</summary>
  private IReadOnlyList<string> JavaScriptOutcomes(string path, string level, IReadOnlyList<PrototypeCall> calls)
    => Toolchain.NodeOutcomes(this._directory, path, calls, true, level == "es5" ? "plain" : "native");

  private static IReadOnlyList<string> Interpreted(IrModule module, IReadOnlyList<PrototypeCall> calls)
    => [.. calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call))];

  private (string Path, string Text) TranslateTo(string source, string to, string profile, string mode, bool inProcess = false)
    => this.Translate(inProcess, source, "csharp", to, "--profile", profile, "--float", mode);

  /// <summary>Every C# profile and every JavaScript level, by the value of <c>--to</c> and of <c>--profile</c>.</summary>
  private static IEnumerable<(string To, string Profile)> Targets()
    => CSharpProfile.All.Select(profile => ("csharp", profile.Name)).Concat(_levels.Select(level => ("javascript", level)));

  private static IEnumerable<TestCaseData> TargetCases() => Targets().Select(target => new TestCaseData(target.To, target.Profile));

  private static IEnumerable<TestCaseData> ModesAndTargets()
    => from mode in _modes from target in Targets() select new TestCaseData(mode, target.To, target.Profile);

  private static IEnumerable<TestCaseData> CSharpProfileNames() => CSharpProfile.All.Select(profile => new TestCaseData(profile.Name));

  private IReadOnlyList<string> Run(string to, string profile, (string Path, string Text) generated, IReadOnlyList<PrototypeCall> calls)
    => to == "csharp" ? CSharpOutcomes(generated.Text, calls) : this.JavaScriptOutcomes(generated.Path, profile, calls);

  // ---------------------------------------------------------------- the scenarios

  /// <summary>Scenario outline: In strict mode every target returns the same bits.</summary>
  [TestCase("csharp", "net2.0")]
  [TestCase("csharp", "netstandard2.0")]
  [TestCase("csharp", "net10.0")]
  [TestCase("javascript", "es5")]
  [TestCase("javascript", "es2022")]
  public void StrictMode_EveryTargetReturnsTheSameBits(string to, string profile) {
    var generated = this.TranslateTo(this.Fixture("float/Dct8"), to, profile, "strict");
    double[] samples = [1, 2, 3, 4, 5, 6, 7, 8];
    PrototypeCall[] calls = [
      new("Transform", [samples, new double[8]]),
      new("TransformSingle", [samples.Select(sample => (float)sample).ToArray(), new float[8]])
    ];
    var expected = Interpreted(PrototypeFixture.Imported("float/Dct8"), calls);
    Assert.That(this.Run(to, profile, generated, calls), Is.EqualTo(expected), generated.Text[..200]);
    Assert.That(generated.Text, Does.Contain("Floating point: strict"));
  }

  /// <summary>Scenario: In accurate mode a native sine is used and its difference is bounded and logged.</summary>
  [Test]
  public void AccurateMode_UsesTheNativeSine_WithinItsBound() {
    var generated = this.Translate(this.Snippet("public static double F(double x) => System.Math.Sin(x);"), "csharp", "javascript", "--profile", "es2022");
    var bound = NativeAccuracy.Bound(IrIntrinsicOp.Sin, NativeAccuracy.Slack(NativeAccuracy.Node, IrIntrinsicOp.Sin, IrType.F64)!.Value);
    Assert.That(generated.Text, Does.Contain("return Math.sin(x);").And.Contain($"//   Sin of f64: ulp(1), slack 1 on node, bound {bound}"));
    var report = this.NativeAgainstVectors("sin", IrType.F64, bound, calls => this.JavaScriptOutcomes(generated.Path, "es2022", calls));
    TestContext.Out.WriteLine($"Math.sin of node against sin.f64: {report}");
  }

  /// <summary>Scenario: Special values are exact everywhere - in every mode and every profile of both backends.</summary>
  [TestCaseSource(nameof(ModesAndTargets))]
  public void SpecialValues_AreExactEverywhere(string mode, string to, string profile) {
    var source = this.Snippet(
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
    Assert.That(Interpreted(PrototypeFixture.Import("Snippet.cs", File.ReadAllText(Path.Combine(this._directory, source))), calls), Is.EqualTo(expected));
    Assert.That(this.Run(to, profile, this.TranslateTo(source, to, profile, mode), calls), Is.EqualTo(expected), $"{profile} {mode}");
  }

  /// <summary>Scenario: JavaScript's power keeps its own special cases through C#.</summary>
  [Test]
  public void JavaScriptPower_KeepsItsSpecialCasesThroughCSharp() {
    var source = this.Script("pow.mjs", "/** @param {number} x @param {number} y @returns {number} */\nexport function f(x, y) { return Math.pow(x, y); }\n");
    PrototypeCall[] calls = [new("f", [1.0, double.NaN]), new("f", [-1.0, double.PositiveInfinity]), new("f", [2.0, 10.0])];
    var node = Toolchain.NodeOutcomes(this._directory, source, calls, true);
    Assert.That(node, Is.EqualTo(new[] { "NaN", "NaN", "1024" }));
    Assert.That(Math.Pow(1.0, double.NaN), Is.EqualTo(1.0), "C#'s Math.Pow is IEEE 754's");
    foreach (var mode in _modes) {
      var generated = this.Translate(source, "javascript", "csharp", "--float", mode);
      Assert.That(CSharpOutcomes(generated.Text, calls), Is.EqualTo(node), mode);
    }
  }

  /// <summary>
  /// Scenario: Single precision is not double precision rounded - the strict binary32 exponential of
  /// every profile of both backends is the correctly rounded one on every vector.
  /// </summary>
  [TestCaseSource(nameof(TargetCases))]
  public void SinglePrecision_IsTheCorrectlyRoundedBinary32Result(string to, string profile) {
    var source = this.Snippet("public static float F(float x) => System.MathF.Exp(x);");
    var vectors = FloatVectors.Of("exp", IrType.F32);
    var calls = vectors.Select(vector => new PrototypeCall("F", [(float)FloatVectors.Value(IrType.F32, vector.Operands[0])])).ToList();
    var expected = vectors.Select(vector => PrototypeFixture.Show(FloatVectors.Value(IrType.F32, vector.Result))).ToList();
    Assert.That(this.Run(to, profile, this.TranslateTo(source, to, profile, "strict"), calls), Is.EqualTo(expected), profile);
  }

  /// <summary>Scenario: JavaScript rounding of a tie goes up - in node and in every C# profile.</summary>
  [TestCaseSource(nameof(CSharpProfileNames))]
  public void JavaScriptRounding_OfATieGoesUp(string profile) {
    var source = this.Script("round.mjs", "export function f(x) { return Math.round(x); }\n");
    PrototypeCall[] calls = [new("f", [-2.5]), new("f", [2.5])];
    Assert.That(Toolchain.NodeOutcomes(this._directory, source, calls, true), Is.EqualTo(new[] { "-2", "3" }));
    var generated = this.Translate(source, "javascript", "csharp", "--profile", profile);
    Assert.That(CSharpOutcomes(generated.Text, calls), Is.EqualTo(new[] { "-2", "3" }), profile);
  }

  /// <summary>Scenario: Random numbers are refused.</summary>
  [Test]
  public void RandomNumbers_AreRefused() {
    var source = this.Script("random.mjs", "export function f() { return Math.random(); }\n");
    var output = Path.Combine(this._directory, "out");
    var (exitCode, text) = Toolchain.RunPolypiler(this._directory, ["translate", source, "--from", "javascript", "--to", "csharp", "--output", output]);
    Assert.That(exitCode, Is.EqualTo(3));
    Assert.That(text, Does.Contain("PLY1102").And.Contain("Math.random"));
    Assert.That(Directory.Exists(output), Is.False);
  }

  // ---------------------------------------------------------------- the test design

  /// <summary>
  /// Test design 1: the three algorithms, translated to both targets, in every profile and both modes:
  /// in strict the bits of the interpreter, in accurate within the bound the fixture declares - a
  /// relative 1e-12 for double and 1e-5 for float, because the results are sums and products of
  /// native results whose error grows with the number of terms (ADR 0005, "declared").
  /// </summary>
  [TestCaseSource(nameof(Algorithms))]
  public void Algorithm_InEveryProfileAndMode(string fixture, string mode, string to, string profile) {
    var calls = AlgorithmCalls(fixture);
    var module = PrototypeFixture.Imported(fixture);
    var expected = Interpreted(module, calls);
    var source = this.Fixture(fixture);
    var timer = Stopwatch.StartNew();
    var generated = this.TranslateTo(source, to, profile, mode, true);
    timer.Stop();
    var actual = this.Run(to, profile, generated, calls);
    for (var index = 0; index < calls.Count; ++index)
      if (mode == "strict")
        Assert.That(actual[index], Is.EqualTo(expected[index]), $"{to} {profile} {mode} {calls[index]}");
      else
        Assert.That(Within(actual[index], expected[index], calls[index].Method.EndsWith("Single", StringComparison.Ordinal) ? 1e-5 : 1e-12), Is.True,
          $"{to} {profile} {mode} {calls[index]}: {actual[index]} against {expected[index]}");

    TestContext.Out.WriteLine($"{fixture} {mode} {to} {profile}: translated in {timer.Elapsed.TotalMilliseconds:F0} ms");
  }

  private static IEnumerable<TestCaseData> Algorithms()
    => from fixture in new[] { "float/Dct8", "float/BoxMuller", "float/Haversine" }
       from mode in _modes
       from target in Targets()
       select new TestCaseData(fixture, mode, target.To, target.Profile);

  /// <summary>
  /// Test design 3: every native function a backend admits, on every reference vector of its
  /// function and format, is within the bound of ADR 0005 of the correctly rounded value; zeros keep
  /// their sign, an infinity and a NaN match exactly. The count of results that are not the correctly
  /// rounded one is recorded.
  /// </summary>
  [TestCaseSource(nameof(NativeFunctions))]
  public void NativeFunctions_AreWithinTheirBound(string to, string profile, string name, string vectors) {
    var runtime = to == "csharp" ? NativeAccuracy.DotNet : NativeAccuracy.Node;
    foreach (var format in new[] { IrType.F64, IrType.F32 }) {
      var op = Enum.Parse<IrIntrinsicOp>(name);
      var operation = FloatOperationTable.Of(op)!;
      var intrinsic = new IrIntrinsic(op, format, [.. Enumerable.Repeat<IrExpr>(new IrConst(format, 0), operation.Shape == FloatShape.Binary ? 2 : 1)]);
      var native = to == "csharp"
        ? CSharpEmitter.Writes(intrinsic, CSharpProfile.Parse(profile, null), FloatMode.Accurate)
        : JavaScriptEmitter.Writes(intrinsic, JavaScriptProfile.Parse(profile), FloatMode.Accurate);
      if (!native)
        continue;

      var bound = NativeAccuracy.Bound(op, NativeAccuracy.Slack(runtime, op, format)!.Value);
      var type = format == IrType.F32 ? "float" : "double";
      var arguments = operation.Shape == FloatShape.Binary ? $"{type} x, {type} y" : $"{type} x";
      var call = name == "PowEcma" ? null : $"System.{(format == IrType.F32 ? "MathF" : "Math")}.{name}({(operation.Shape == FloatShape.Binary ? "x, y" : "x")})";
      if (name == "Hypot")
        call = $"System.{(format == IrType.F32 ? "Single" : "Double")}.Hypot(x, y)";

      (string Path, string Text) generated;
      if (call is null) {
        if (format == IrType.F32)
          continue;

        generated = this.Translate(true, this.Script("powecma.mjs", "/** @param {number} x @param {number} y @returns {number} */\nexport function F(x, y) { return x ** y; }\n"),
          "javascript", to, "--profile", profile);
      } else {
        generated = this.Translate(true, this.Snippet($"public static {type} F({arguments}) => {call};"), "csharp", to, "--profile", profile);
      }

      var report = this.NativeAgainstVectors(vectors, format, bound,
        calls => to == "csharp" ? CSharpOutcomes(generated.Text, calls) : this.JavaScriptOutcomes(generated.Path, profile, calls), name == "PowEcma");
      TestContext.Out.WriteLine($"{to} {profile} {name} {format}: bound {bound}, {report}");
    }
  }

  /// <summary>The two runtimes of test design 3, each with every native function by its name and the name of its vectors.</summary>
  private static IEnumerable<TestCaseData> NativeFunctions() {
    var functions = new Dictionary<string, string> {
      ["Sin"] = "sin", ["Cos"] = "cos", ["Tan"] = "tan", ["Exp"] = "exp", ["Log"] = "log", ["Log2"] = "log2", ["Log10"] = "log10",
      ["Pow"] = "pow", ["PowEcma"] = "pow", ["Atan2"] = "atan2", ["Sinh"] = "sinh", ["Cosh"] = "cosh", ["Tanh"] = "tanh", ["Cbrt"] = "cbrt", ["Hypot"] = "hypot"
    };
    return from runtime in new[] { ("csharp", "net10.0"), ("javascript", "es2022") }
           from function in functions
           select new TestCaseData(runtime.Item1, runtime.Item2, function.Key, function.Value);
  }

  /// <summary>
  /// Runs every vector of a function through a native function and checks it against the correctly
  /// rounded value: within the bound for a finite non-zero result, exactly for a zero, an infinity and
  /// a NaN. Vectors whose exact result is beyond the overflow threshold of the bound are left out.
  /// </summary>
  private string NativeAgainstVectors(string function, IrType format, int bound, Func<IReadOnlyList<PrototypeCall>, IReadOnlyList<string>> run, bool ecma = false) {
    var vectors = FloatVectors.Of(function, format).Where(vector => !ecma || !EcmaDiffers(format, vector)).ToList();
    var calls = vectors.Select(vector => new PrototypeCall("F", [.. vector.Operands.Select(operand
      => format == IrType.F32 ? (object)(float)FloatVectors.Value(format, operand) : FloatVectors.Value(format, operand))])).ToList();
    var outcomes = run(calls);
    var identical = 0;
    var largest = 0L;
    var failures = new List<string>();
    for (var index = 0; index < vectors.Count; ++index) {
      var vector = vectors[index];
      var expected = FloatVectors.Value(format, vector.Result);
      var actual = Parse(outcomes[index], format);
      var actualBits = FloatVectors.Bits(format, actual);
      if (FloatVectors.Same(format, vector.Result, actualBits)) {
        ++identical;
        continue;
      }

      if (double.IsNaN(expected) || double.IsNaN(actual) || double.IsInfinity(actual) || expected == 0 && actual == 0
          || double.IsInfinity(expected) || vector.Kind == FloatVectorKind.Inexact && double.IsInfinity(FloatVectors.Value(format, vector.Low + 1))) {
        if (!double.IsInfinity(expected) || !(double.IsInfinity(actual) || Math.Abs(actual) == (format == IrType.F32 ? float.MaxValue : double.MaxValue)))
          failures.Add($"{vector}: {outcomes[index]} instead of {PrototypeFixture.Show(expected)}");

        continue;
      }

      var distance = FloatVectors.Distance(format, vector.Result, actualBits);
      largest = Math.Max(largest, distance);
      if (distance > bound)
        failures.Add($"{vector}: distance {distance}");
    }

    Assert.That(failures, Is.Empty, $"{function} {format}: {string.Join("; ", failures.Take(10))}");
    return $"{vectors.Count} vectors, {vectors.Count - identical} not identical, largest distance {largest}";
  }

  /// <summary>Whether ECMA-262's exponentiation differs from IEEE 754's for the operands of a vector of pow.</summary>
  private static bool EcmaDiffers(IrType format, FloatVector vector) {
    var x = FloatVectors.Value(format, vector.Operands[0]);
    var y = FloatVectors.Value(format, vector.Operands[1]);
    return double.IsNaN(y) || Math.Abs(x) == 1 && double.IsInfinity(y);
  }

  /// <summary>A number as an outcome shows it, back to the number.</summary>
  private static double Parse(string outcome, IrType format) {
    if (outcome == "NaN")
      return double.NaN;

    var value = outcome.StartsWith("f:", StringComparison.Ordinal)
      ? BitConverter.Int64BitsToDouble(Convert.ToInt64(outcome[2..], 16))
      : double.Parse(outcome, System.Globalization.CultureInfo.InvariantCulture);
    return format == IrType.F32 ? (float)value : value;
  }

  /// <summary>Whether two outcomes agree within a relative bound, element by element, NaN with NaN and an infinity with itself.</summary>
  private static bool Within(string actual, string expected, double relative) {
    var a = actual.Replace("[", "").Replace("]", "").Split([" | ", ","], StringSplitOptions.None);
    var b = expected.Replace("[", "").Replace("]", "").Split([" | ", ","], StringSplitOptions.None);
    if (a.Length != b.Length)
      return false;

    for (var index = 0; index < a.Length; ++index) {
      if (a[index] == b[index])
        continue;

      var x = Parse(a[index], IrType.F64);
      var y = Parse(b[index], IrType.F64);
      if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y) || Math.Abs(x - y) > relative * Math.Max(Math.Abs(y), 1e-300))
        return false;
    }

    return true;
  }

  /// <summary>Fixed inputs of the three algorithms: samples, a fixed uniform sequence and pairs of cities.</summary>
  private static List<PrototypeCall> AlgorithmCalls(string fixture) => fixture switch {
    "float/Dct8" => [
      new("Transform", [new double[] { 1, 2, 3, 4, 5, 6, 7, 8 }, new double[8]]),
      new("Transform", [new double[] { 0.5, -1.25, 3.75, 0, -0.0, 1e-3, 255, -128 }, new double[8]]),
      new("TransformSingle", [new float[] { 1, 2, 3, 4, 5, 6, 7, 8 }, new float[8]])
    ],
    "float/BoxMuller" => [
      new("Normals", [new double[] { 0.5, 0.25, 0.9, 0.125, 1e-6, 0.999, 1.0, 0.0 }, new double[8]]),
      new("NormalsSingle", [new float[] { 0.5f, 0.25f, 0.9f, 0.125f, 1e-6f, 0.999f, 1.0f, 0.0f }, new float[8]])
    ],
    _ => [
      new("Distance", [52.5200, 13.4050, 48.8566, 2.3522, 6371.0]),
      new("Distance", [-33.8688, 151.2093, 40.7128, -74.0060, 6371.0]),
      new("Distance", [0.0, 0.0, 0.0, 180.0, 6371.0]),
      new("DistanceSingle", [52.52f, 13.405f, 48.8566f, 2.3522f, 6371.0f])
    ]
  };

}
