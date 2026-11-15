using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
[Layer("core")]
[Category("Prototype")]
public sealed class FloatOperationsTests {

  /// <summary>The two floating-point modes.</summary>
  internal static readonly string[] Modes = ["strict", "accurate"];

  private static readonly string[] _levels = ["es5", "es2015", "es2017", "es2020", "es2022"];

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(this._directory, "float"));
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

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
  internal static IReadOnlyList<string> CSharpOutcomes(string text, IReadOnlyList<PrototypeCall> calls) {
    var build = GeneratedCSharp.Compile(text, false);
    return [.. calls.Select(build.Outcome)];
  }

  /// <summary>The outcomes of calls of a generated JavaScript file in node.</summary>
  private IReadOnlyList<string> JavaScriptOutcomes(string path, string level, IReadOnlyList<PrototypeCall> calls)
    => JavaScriptOutcomes(this._directory, path, level, calls);

  /// <summary>The outcomes of calls of a generated JavaScript file of a directory in node.</summary>
  internal static IReadOnlyList<string> JavaScriptOutcomes(string directory, string path, string level, IReadOnlyList<PrototypeCall> calls)
    => Toolchain.NodeOutcomes(directory, path, calls, true, level == "es5" ? "plain" : "native");

  /// <summary>The outcomes of calls in the interpreter.</summary>
  internal static IReadOnlyList<string> Interpreted(IrModule module, IReadOnlyList<PrototypeCall> calls)
    => [.. calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call))];

  private (string Path, string Text) TranslateTo(string source, string to, string profile, string mode, bool inProcess = false)
    => this.Translate(inProcess, source, "csharp", to, "--profile", profile, "--float", mode);

  /// <summary>Every C# profile and every JavaScript level, by the value of <c>--to</c> and of <c>--profile</c>.</summary>
  internal static IEnumerable<(string To, string Profile)> Targets()
    => CSharpProfile.All.Select(profile => ("csharp", profile.Name)).Concat(_levels.Select(level => ("javascript", level)));

  private IReadOnlyList<string> Run(string to, string profile, (string Path, string Text) generated, IReadOnlyList<PrototypeCall> calls)
    => to == "csharp" ? CSharpOutcomes(generated.Text, calls) : this.JavaScriptOutcomes(generated.Path, profile, calls);

  // ---------------------------------------------------------------- the scenarios

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
       from mode in Modes
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

  /// <summary>Asserts <see cref="CheckNative"/> and returns its report.</summary>
  private string NativeAgainstVectors(string function, IrType format, int bound, Func<IReadOnlyList<PrototypeCall>, IReadOnlyList<string>> run, bool ecma = false) {
    var check = CheckNative(function, format, bound, run, ecma);
    Assert.That(check.Exact.Concat(check.Bounded), Is.Empty, $"{function} {format}: {string.Join("; ", check.Exact.Concat(check.Bounded).Take(10))}");
    return check.Report;
  }

  /// <summary>
  /// Runs every vector of a function through a native function and compares it with the correctly
  /// rounded value: within the bound for a finite non-zero result, exactly for a zero, an infinity and
  /// a NaN. Vectors whose exact result is beyond the overflow threshold of the bound are left out.
  /// </summary>
  /// <returns>The report, the vectors whose zero, NaN or infinity was not exact, those beyond the bound, and the number of vectors whose correct result is a zero, a NaN or an infinity and of the others.</returns>
  internal static (string Report, List<string> Exact, List<string> Bounded, int Special, int Finite) CheckNative(
    string function, IrType format, int bound, Func<IReadOnlyList<PrototypeCall>, IReadOnlyList<string>> run, bool ecma = false) {
    var vectors = FloatVectors.Of(function, format).Where(vector => !ecma || !EcmaDiffers(format, vector)).ToList();
    var calls = vectors.Select(vector => new PrototypeCall("F", [.. vector.Operands.Select(operand
      => format == IrType.F32 ? (object)(float)FloatVectors.Value(format, operand) : FloatVectors.Value(format, operand))])).ToList();
    var outcomes = run(calls);
    var identical = 0;
    var largest = 0L;
    var special = 0;
    var exact = new List<string>();
    var bounded = new List<string>();
    for (var index = 0; index < vectors.Count; ++index) {
      var vector = vectors[index];
      var expected = FloatVectors.Value(format, vector.Result);
      var actual = Parse(outcomes[index], format);
      var actualBits = FloatVectors.Bits(format, actual);
      if (expected == 0 || double.IsNaN(expected) || double.IsInfinity(expected))
        ++special;

      if (FloatVectors.Same(format, vector.Result, actualBits)) {
        ++identical;
        continue;
      }

      if (double.IsNaN(expected) || double.IsNaN(actual) || double.IsInfinity(actual) || expected == 0 && actual == 0
          || double.IsInfinity(expected) || vector.Kind == FloatVectorKind.Inexact && double.IsInfinity(FloatVectors.Value(format, vector.Low + 1))) {
        if (!double.IsInfinity(expected) || !(double.IsInfinity(actual) || Math.Abs(actual) == (format == IrType.F32 ? float.MaxValue : double.MaxValue)))
          exact.Add($"{vector}: {outcomes[index]} instead of {PrototypeFixture.Show(expected)}");

        continue;
      }

      var distance = FloatVectors.Distance(format, vector.Result, actualBits);
      largest = Math.Max(largest, distance);
      if (distance > bound)
        bounded.Add($"{vector}: distance {distance}");
    }

    return ($"{vectors.Count} vectors, {vectors.Count - identical} not identical, largest distance {largest}", exact, bounded, special, vectors.Count - special);
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
