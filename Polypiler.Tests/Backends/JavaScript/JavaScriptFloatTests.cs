using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Backends.JavaScript;

/// <summary>
/// The floating-point operations of #1301 in the JavaScript backend: which ones a level writes as a
/// function of <c>Math</c> in which mode, that every operation of every format can be emitted at every
/// level and in both modes, and that the files use nothing their level lacks.
/// </summary>
[TestFixture]
public sealed class JavaScriptFloatTests {

  private static readonly FloatMode[] _modes = [FloatMode.Strict, FloatMode.Accurate];
  private static readonly IrType[] _formats = [IrType.F32, IrType.F64];

  private static readonly JavaScriptProfile[] _profiles = [
    JavaScriptProfile.Parse("es5"), JavaScriptProfile.Parse("es5", typedArrays: true), JavaScriptProfile.Parse("es2015"),
    JavaScriptProfile.Parse("es2017"), JavaScriptProfile.Parse("es2020"), JavaScriptProfile.Parse("es2022")
  ];

  private static IEnumerable<TestCaseData> Operations
    => FloatOperationTable.All.Where(operation => operation.Double is not null).Select(operation => new TestCaseData(operation).SetArgDisplayNames(operation.Op.ToString()));

  [Test]
  public void AdmittedNativeFunctions_AreTheOnesAdr0005Admits() {
    var admitted = FloatOperationTable.All.Where(operation => operation.Double is not null && operation.Contract.Kind == FloatContractKind.Ulp)
      .SelectMany(operation => _formats.Select(format => (operation, format)))
      .Where(pair => JavaScriptEmitter.Writes(Intrinsic(pair.operation, pair.format), JavaScriptProfile.Default, FloatMode.Accurate))
      .Select(pair => $"{pair.operation.Op} {pair.format}");
    Assert.That(admitted, Is.EquivalentTo(new[] {
      "Sin f64", "Cos f64", "Tan f64", "Exp f64", "Log f64", "Log2 f64", "PowEcma f64", "Sinh f64", "Cosh f64", "Tanh f64", "Cbrt f64",
      "Sin f32", "Cos f32", "Tan f32", "Exp f32", "Log f32", "Log2 f32", "Log10 f32", "PowEcma f32", "Atan2 f32", "Sinh f32", "Cosh f32",
      "Tanh f32", "Cbrt f32", "Hypot f32"
    }));
  }

  /// <summary>
  /// Test design 10: a function of ECMAScript 2015 is native from that level on and the portable
  /// implementation below it; no function is native in strict, no exact operation ever.
  /// </summary>
  [TestCaseSource(nameof(Operations))]
  public void Writes_IsTheFunctionOfMathWhereTheLevelHasIt(object entry) {
    var operation = (FloatOperation)entry;
    foreach (var format in _formats)
      foreach (var profile in _profiles) {
        var intrinsic = Intrinsic(operation, format);
        Assert.That(JavaScriptEmitter.Writes(intrinsic, profile, FloatMode.Strict), Is.EqualTo(operation.Traps is not null), $"{format} {profile} strict");
        if (operation.Contract.Kind == FloatContractKind.Exact)
          Assert.That(JavaScriptEmitter.Writes(intrinsic, profile, FloatMode.Accurate), Is.EqualTo(operation.Traps is not null), $"{format} {profile}");
      }
  }

  [TestCase(IrIntrinsicOp.Log2, "es5", false)]
  [TestCase(IrIntrinsicOp.Log2, "es2015", true)]
  [TestCase(IrIntrinsicOp.Sin, "es5", true)]
  [TestCase(IrIntrinsicOp.Atan2, "es2022", false)]
  [TestCase(IrIntrinsicOp.Pow, "es2022", false)]
  public void ANativeFunction_BeginsAtItsLevel(IrIntrinsicOp op, string level, bool native)
    => Assert.That(JavaScriptEmitter.Writes(Intrinsic(FloatOperationTable.Of(op)!, IrType.F64), JavaScriptProfile.Parse(level), FloatMode.Accurate), Is.EqualTo(native));

  /// <summary>Test design 14: every operation, both formats, every level, both modes: no <c>PLY1152</c>, and nothing the level lacks.</summary>
  [TestCaseSource(nameof(Operations))]
  public void EveryOperation_CanBeEmittedAtEveryLevelAndMode(object entry) {
    var operation = (FloatOperation)entry;
    foreach (var format in _formats)
      foreach (var profile in _profiles)
        foreach (var mode in _modes) {
          var module = JavaScriptEmitter.Legalize(FloatIntrinsics.Module(operation, format), () => FloatImplementations.Module, profile, mode);
          Assert.That(JavaScriptEmitter.Check(module, profile), Is.Empty, $"{format} {profile} {mode}");
        }
  }

  [Test, Category("Slow")]
  public void EveryLevelAndMode_UsesOnlyWhatTheLevelHas() {
    var module = new IrModule("Everything", [
      .. FloatOperationTable.All.Where(operation => operation.Double is not null)
        .SelectMany(operation => _formats.Select(format => FloatIntrinsics.Module(operation, format, 2).Functions[0] with { Name = $"{operation.Op}_{format}" }))
    ]);
    foreach (var profile in _profiles)
      foreach (var mode in _modes) {
        var text = JavaScriptEmitter.Emit(JavaScriptEmitter.Legalize(module, () => FloatImplementations.Module, profile, mode), profile, mode).Single().Content;
        Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, $"{profile} {mode}");
      }
  }

  [Test]
  public void Header_NamesTheModeAndEveryNativeFunctionWithItsBound() {
    var sin = FloatIntrinsics.Module(FloatOperationTable.Of(IrIntrinsicOp.Sin)!, IrType.F32);
    var profile = JavaScriptProfile.Default;
    var accurate = JavaScriptEmitter.Emit(JavaScriptEmitter.Legalize(sin, () => FloatImplementations.Module, profile, FloatMode.Accurate), profile, FloatMode.Accurate).Single().Content;
    Assert.That(accurate, Does.Contain("//   Sin of f32: ulp(1), slack 1 on node, bound 3").And.Contain("Math.fround(Math.sin(p0))"));
    var strict = JavaScriptEmitter.Emit(JavaScriptEmitter.Legalize(sin, () => FloatImplementations.Module, profile, FloatMode.Strict), profile, FloatMode.Strict).Single().Content;
    Assert.That(strict, Does.Contain("// Floating point: strict (ADR 0005).").And.Not.Contain("Math.sin("));
  }

  [Test]
  public void AnOperationWithoutAFunctionOfMath_IsRefusedUnlessItIsLegalized() {
    var module = FloatIntrinsics.Module(FloatOperationTable.Of(IrIntrinsicOp.CopySign)!, IrType.F64);
    Assert.That(JavaScriptEmitter.Check(module, JavaScriptProfile.Default).Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { JavaScriptEmitter.Inexpressible }));
  }

  [Test]
  public void TheOperationsThatTrap_AreHelpers() {
    var clamp = JavaScriptEmitter.Emit(FloatIntrinsics.Module(FloatOperationTable.Of(IrIntrinsicOp.Clamp)!, IrType.F64), JavaScriptProfile.Default).Single().Content;
    Assert.That(clamp, Does.Contain("function $clamp(value, min, max)").And.Contain("return $clamp(p0, p1, p2);"));
    var round = JavaScriptEmitter.Emit(FloatIntrinsics.Module(FloatOperationTable.Of(IrIntrinsicOp.RoundDigits)!, IrType.F32, 3), JavaScriptProfile.Parse("es5")).Single().Content;
    Assert.That(round, Does.Contain("function $roundd32(value, digits, mode)").And.Contain("$roundd32(p0, p1, 3)").And.Contain("$fround("));
  }

  private static IrIntrinsic Intrinsic(FloatOperation operation, IrType format)
    => (IrIntrinsic)((IrReturn)FloatIntrinsics.Module(operation, format).Functions[0].Body.Statements[0]).Value!;

}
