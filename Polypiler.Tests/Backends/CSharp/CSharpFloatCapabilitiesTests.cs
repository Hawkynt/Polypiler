using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Backends.CSharp;

/// <summary>
/// The floating-point rows of the C# capability table (#1301): where each begins, which ones the
/// emitter writes in which profile and mode, that every operation of every format can be emitted in
/// every profile and both modes, and that what is emitted compiles and computes what the IR computes.
/// </summary>
[TestFixture]
public sealed class CSharpFloatCapabilitiesTests {

  private static readonly FloatMode[] _modes = [FloatMode.Strict, FloatMode.Accurate];
  private static readonly IrType[] _formats = [IrType.F32, IrType.F64];

  private static IEnumerable<CSharpCapability> FloatRows => CSharpCapabilities.Default.Rows.Where(CSharpCapabilitiesTests.IsFloating);

  private static IEnumerable<TestCaseData> NativeRows
    => FloatRows.Where(row => row.Since is not null && row.Since != CSharpProfile.Net20).DistinctBy(row => (row.Native!.Method, row.Since))
      .Select(row => new TestCaseData(row).SetArgDisplayNames(row.Name));

  private static IEnumerable<TestCaseData> Operations
    => FloatOperationTable.All.Where(operation => operation.Double is not null).Select(operation => new TestCaseData(operation).SetArgDisplayNames(operation.Op.ToString()));

  private static CSharpProfile? Before(CSharpProfile profile) => CSharpProfile.All.TakeWhile(candidate => candidate != profile).LastOrDefault();

  /// <summary>
  /// The first profile of a row is the one of the reference assemblies: a call of the API compiles in
  /// the row's profile and not in the one before.
  /// </summary>
  [TestCaseSource(nameof(NativeRows))]
  public void Row_BeginsWhereTheFrameworkHasTheApi(object entry) {
    var row = (CSharpCapability)entry;
    var operation = FloatOperationTable.Of(row.Operation!.Value)!;
    var type = row.Operands[0] == IrTypeKind.F32 && row.Native!.Cast is null ? "float" : "double";
    var parameters = FloatIntrinsics.Operands(operation.Shape, row.Operands[0] == IrTypeKind.F32 ? IrType.F32 : IrType.F64)
      .Select((parameter, index) => (parameter == IrType.I32 ? "int" : type) + " p" + index).ToList();
    var returns = operation.Shape switch { FloatShape.ToInteger => "int", FloatShape.Predicate => "bool", _ => type };
    var source = $"static class C {{ static {returns} F({string.Join(", ", parameters)}) {{ return {row.Native!.Method}({string.Join(", ", parameters.Select(parameter => parameter.Split(' ')[1]))}); }} }}";
    var language = row.Since!.LanguageVersionName;
    Assert.That(GeneratedCSharp.ComplaintsFor(source, row.Since.Name, language), Is.Empty, source);
    Assert.That(GeneratedCSharp.ComplaintsFor(source, Before(row.Since)!.Name, language), Is.Not.Empty, source);
  }

  /// <summary>
  /// Test design 10: an exact operation is written natively from the first profile of its row on and
  /// is the portable implementation before; an operation with an <c>ulp(n)</c> contract only in the
  /// mode accurate and where .NET's function is admitted, and never in strict.
  /// </summary>
  [TestCaseSource(nameof(Operations))]
  public void Writes_IsTheRowWhereTheProfileHasIt(object entry) {
    var operation = (FloatOperation)entry;
    foreach (var format in _formats) {
      var intrinsic = Intrinsic(operation, format);
      var row = CSharpCapabilities.Default.Of(operation.Op, format.Kind);
      foreach (var profile in CSharpProfile.All) {
        var has = row is not null && row.IsIn(profile);
        var exact = operation.Contract.Kind == FloatContractKind.Exact;
        var admitted = NativeAccuracy.Slack(NativeAccuracy.DotNet, operation.Op, format) is not null;
        // Clamp and RoundDigits trap: they are never the portable implementation, but the row or its helper.
        var own = operation.Traps is not null;
        Assert.That(CSharpEmitter.Writes(intrinsic, profile, FloatMode.Accurate), Is.EqualTo(own || has && (exact || admitted)), $"{format} {profile} accurate");
        Assert.That(CSharpEmitter.Writes(intrinsic, profile, FloatMode.Strict), Is.EqualTo(own || has && exact), $"{format} {profile} strict");
      }
    }
  }

  /// <summary>
  /// Test design 13: a native function without a measurement, or whose error exceeds the cap or the
  /// reference bound, is not written; the portable implementation is.
  /// </summary>
  [TestCase(IrIntrinsicOp.Asin, "f64", "no measurement")]
  [TestCase(IrIntrinsicOp.Sinh, "f64", "glibc 2.44 sinh, published at 1.94e19 ULP")]
  [TestCase(IrIntrinsicOp.Sinh, "f32", "the double rounded is no bound when the double is that far off")]
  [TestCase(IrIntrinsicOp.Tan, "f64", "Microsoft's C runtime, published at 1.32 ULP, above ulp(1)")]
  [TestCase(IrIntrinsicOp.Cbrt, "f64", "glibc 2.44 cbrt, 3.67 ULP")]
  [TestCase(IrIntrinsicOp.Log10, "f64", "1.62 ULP, above ulp(1)")]
  public void NativeFunctionThatIsNotAdmitted_IsThePortableImplementation(IrIntrinsicOp op, string format, string why) {
    var type = format == "f32" ? IrType.F32 : IrType.F64;
    Assert.That(NativeAccuracy.Slack(NativeAccuracy.DotNet, op, type), Is.Null, why);
    Assert.That(CSharpEmitter.Writes(Intrinsic(FloatOperationTable.Of(op)!, type), CSharpProfile.Net100, FloatMode.Accurate), Is.False, why);
  }

  [Test]
  public void AdmittedNativeFunctions_AreTheOnesAdr0005Admits() {
    var admitted = FloatOperationTable.All.Where(operation => operation.Double is not null)
      .SelectMany(operation => _formats.Select(format => (operation, format)))
      .Where(pair => CSharpEmitter.Writes(Intrinsic(pair.operation, pair.format), CSharpProfile.Net100, FloatMode.Accurate)
        && pair.operation.Contract.Kind == FloatContractKind.Ulp)
      .Select(pair => $"{pair.operation.Op} {pair.format}");
    Assert.That(admitted, Is.EquivalentTo(new[] {
      "Sin f64", "Cos f64", "Log f64", "Log2 f64", "Pow f64", "Atan2 f64", "Cosh f64", "Tanh f64", "Hypot f64",
      "Sin f32", "Cos f32", "Tan f32", "Exp f32", "Log f32", "Log2 f32", "Log10 f32", "Pow f32", "Atan2 f32", "Cosh f32", "Tanh f32", "Cbrt f32", "Hypot f32"
    }));
  }

  /// <summary>
  /// Test design 14: every operation of the table, in both formats, in every profile and both modes,
  /// can be emitted once the portable implementations are put in: no <c>PLY1053</c>, no <c>PLY1052</c>.
  /// </summary>
  [TestCaseSource(nameof(Operations))]
  public void EveryOperation_CanBeEmittedInEveryProfileAndMode(object entry) {
    var operation = (FloatOperation)entry;
    foreach (var format in _formats)
      foreach (var profile in CSharpProfile.All)
        foreach (var mode in _modes) {
          var module = CSharpEmitter.Legalize(Module(operation, format), () => FloatImplementations.Module, profile, mode);
          Assert.That(CSharpEmitter.Check(module, profile), Is.Empty, $"{format} {profile} {mode}");
        }
  }

  [Test]
  public void Header_NamesTheModeAndEveryNativeFunctionWithItsBound() {
    var sin = Module(FloatOperationTable.Of(IrIntrinsicOp.Sin)!, IrType.F64);
    var accurate = CSharpEmitter.Emit(CSharpEmitter.Legalize(sin, () => FloatImplementations.Module, CSharpProfile.Net100, FloatMode.Accurate), CSharpProfile.Net100, FloatMode.Accurate).Single().Content;
    Assert.That(accurate, Does.Contain("// Floating point: accurate (ADR 0005).").And.Contain("//   Sin of f64: ulp(1), slack 1 on dotnet-sdk, bound 3").And.Contain("global::System.Math.Sin(p0)"));
    var strict = CSharpEmitter.Emit(CSharpEmitter.Legalize(sin, () => FloatImplementations.Module, CSharpProfile.Net100, FloatMode.Strict), CSharpProfile.Net100, FloatMode.Strict).Single().Content;
    Assert.That(strict, Does.Contain("// Floating point: strict (ADR 0005). Every function is the portable implementation").And.Not.Contain("Math.Sin("));
    var integers = new IrModule("M", [new IrFunction("F", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 1))]))]);
    Assert.That(CSharpEmitter.Emit(integers, CSharpProfile.Net100, FloatMode.Strict).Single().Content, Does.Not.Contain("Floating point"));
  }

  /// <summary>
  /// Every profile and mode compiles against its reference assemblies a class that uses every
  /// operation of the table, and on the running runtime the class computes, in strict, the bits the
  /// interpreter computes - in accurate, the same bits wherever no native function with a tolerance is
  /// called.
  /// </summary>
  [Test, Category("Slow")]
  public void EveryProfileAndMode_CompilesAndComputesWhatTheInterpreterComputes([ValueSource(typeof(CSharpProfile), nameof(CSharpProfile.All))] CSharpProfile profile) {
    var module = Everything();
    foreach (var mode in _modes) {
      var lowered = CSharpEmitter.Legalize(module, () => FloatImplementations.Module, profile, mode);
      var text = CSharpEmitter.Emit(lowered, profile, mode).Single().Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile} {mode}");
      var build = GeneratedCSharp.Compile(text, false);
      foreach (var function in module.Functions) {
        var op = Enum.Parse<IrIntrinsicOp>(function.Name.Split('_')[0]);
        var format = function.Parameters[0].Type;
        if (mode == FloatMode.Accurate && FloatOperationTable.Of(op)!.Contract.Kind == FloatContractKind.Ulp && CSharpEmitter.Writes(Intrinsic(FloatOperationTable.Of(op)!, format), profile, mode))
          continue;

        foreach (var operands in Operands(function)) {
          var expected = IrInterpreter.Call(module, function.Name, operands);
          var actual = build.Method(function.Name).Invoke(null, operands);
          Assert.That(Same(expected, actual), Is.True, $"{profile} {mode} {function.Name}({string.Join(", ", operands)}): {expected} {actual}");
        }
      }
    }
  }

  private static bool Same(object? a, object? b) => (a, b) switch {
    (double x, double y) => double.IsNaN(x) ? double.IsNaN(y) : BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y),
    (float x, float y) => float.IsNaN(x) ? float.IsNaN(y) : BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y),
    _ => Equals(a, b)
  };

  /// <summary>A few operands of every class for a function of <see cref="Everything"/>.</summary>
  private static IEnumerable<object[]> Operands(IrFunction function) {
    double[] values = [0.0, -0.0, 0.5, -1.0, 1.0, 2.5, -3.75, 100.0, 1e-310, 1e300, double.PositiveInfinity, double.NaN];
    var types = function.Parameters.Select(parameter => parameter.Type).ToList();
    for (var index = 0; index < values.Length; ++index)
      yield return [.. types.Select((type, position) => {
        var value = values[(index + 3 * position) % values.Length];
        return type == IrType.F32 ? (object)(float)value : type == IrType.F64 ? value : (object)(position == 1 && FloatOperationTable.Of(Enum.Parse<IrIntrinsicOp>(function.Name.Split('_')[0]))!.Op == IrIntrinsicOp.RoundDigits ? 2 : index - 3);
      })];
  }

  /// <summary>One function per operation and format, named <c>Op_format</c>; a rounding to digits in the mode to positive infinity.</summary>
  private static IrModule Everything() => new("Everything", [
    .. FloatOperationTable.All.Where(operation => operation.Double is not null && operation.Op != IrIntrinsicOp.Clamp)
      .SelectMany(operation => _formats.Select(format => Module(operation, format, 4).Functions[0] with { Name = $"{operation.Op}_{format}" }))
  ]);

  private static IrModule Module(FloatOperation operation, IrType format, int mode = 0) => FloatIntrinsics.Module(operation, format, mode);

  private static IrIntrinsic Intrinsic(FloatOperation operation, IrType format)
    => (IrIntrinsic)((IrReturn)Module(operation, format).Functions[0].Body.Statements[0]).Value!;

}
