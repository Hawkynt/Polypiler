using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Tests.Helpers.Support;

namespace Polypiler.Tests.Ieee;

/// <summary>
/// #116: the helpers that compute the IEEE operations on integers for targets that round only to
/// nearest even (<c>AddRounded64</c>, …, <c>CompareFloat</c>, <c>ClassifyFloat32</c>/<c>64</c>) and the
/// legalization that puts them in. The helpers, compiled, agree bit for bit with the exact arithmetic
/// of <see cref="IeeeFormat"/> over every range of exponents, subnormal results and overflow included;
/// the legalized module, imported and interpreted, agrees with the node.
/// </summary>
[TestFixture]
public sealed class IeeeLegalizationTests {

  private static readonly double[] _specials = [
    0.0, -0.0, double.Epsilon, -double.Epsilon, double.MaxValue, -double.MaxValue, double.PositiveInfinity, double.NegativeInfinity, double.NaN,
    1.0, -1.0, 3.0, 0.1, BitConverter.Int64BitsToDouble(0x0010000000000000L), BitConverter.Int64BitsToDouble(0x000FFFFFFFFFFFFFL), 9007199254740993.0, 1e308, 1e-308
  ];

  private static IEnumerable<(double, double)> Pairs(int seed) {
    foreach (var left in _specials)
      foreach (var right in _specials)
        yield return (left, right);

    var random = new Random(seed);
    for (var index = 0; index < 20000; ++index) {
      // Every exponent, so that sums cancel, products and quotients overflow and underflow.
      yield return (Random(random), Random(random));
    }
  }

  private static double Random(Random random) => random.Next(8) switch {
    0 => BitConverter.Int64BitsToDouble(random.NextInt64(0, 0x0020000000000000L)) * (random.Next(2) == 0 ? 1 : -1),
    1 => BitConverter.Int64BitsToDouble(random.NextInt64()),
    _ => (random.NextDouble() + 0.5) * Math.Pow(2, random.Next(-1100, 1030)) * (random.Next(2) == 0 ? 1 : -1)
  };

  private static ulong Expected(double value) => double.IsNaN(value) ? IeeeFormats.Binary64.CanonicalNaN : BitConverter.DoubleToUInt64Bits(value);

  private static ulong Expected(float value) => float.IsNaN(value) ? IeeeFormats.Binary32.CanonicalNaN : BitConverter.SingleToUInt32Bits(value);

  [Test]
  public void DoubleHelpers_AgreeWithTheExactArithmetic() {
    var format = IeeeFormats.Binary64;
    foreach (var (left, right) in Pairs(1162)) {
      var (a, b) = (BitConverter.DoubleToUInt64Bits(left), BitConverter.DoubleToUInt64Bits(right));
      for (var mode = 0; mode < 5; ++mode) {
        var rounding = (RoundingMode)mode;
        Assert.That(Expected(HelperOperations.AddRounded64(left, right, mode)), Is.EqualTo(format.Add(a, b, rounding)), $"{left:R} + {right:R} in {rounding}");
        Assert.That(Expected(HelperOperations.SubtractRounded64(left, right, mode)), Is.EqualTo(format.Subtract(a, b, rounding)), $"{left:R} - {right:R} in {rounding}");
        Assert.That(Expected(HelperOperations.MultiplyRounded64(left, right, mode)), Is.EqualTo(format.Multiply(a, b, rounding)), $"{left:R} * {right:R} in {rounding}");
        Assert.That(Expected(HelperOperations.DivideRounded64(left, right, mode)), Is.EqualTo(format.Divide(a, b, rounding)), $"{left:R} / {right:R} in {rounding}");
      }
    }
  }

  [Test]
  public void SingleHelpers_AgreeWithTheExactArithmetic() {
    var format = IeeeFormats.Binary32;
    var random = new Random(1163);
    var specials = _specials.Select(value => (float)value).Concat([float.Epsilon, float.MaxValue, BitConverter.Int32BitsToSingle(0x00800000), 16777217f]).ToArray();
    var pairs = specials.SelectMany(left => specials.Select(right => (left, right)))
      .Concat(Enumerable.Range(0, 20000).Select(_ => (BitConverter.Int32BitsToSingle(random.Next() * (random.Next(2) == 0 ? 1 : -1)), BitConverter.Int32BitsToSingle(random.Next()))));
    foreach (var (left, right) in pairs) {
      var (a, b) = ((ulong)BitConverter.SingleToUInt32Bits(left), (ulong)BitConverter.SingleToUInt32Bits(right));
      for (var mode = 0; mode < 5; ++mode) {
        var rounding = (RoundingMode)mode;
        Assert.That(Expected(HelperOperations.AddRounded32(left, right, mode)), Is.EqualTo(format.Add(a, b, rounding)), $"{left:R} + {right:R} in {rounding}");
        Assert.That(Expected(HelperOperations.SubtractRounded32(left, right, mode)), Is.EqualTo(format.Subtract(a, b, rounding)), $"{left:R} - {right:R} in {rounding}");
        Assert.That(Expected(HelperOperations.MultiplyRounded32(left, right, mode)), Is.EqualTo(format.Multiply(a, b, rounding)), $"{left:R} * {right:R} in {rounding}");
        Assert.That(Expected(HelperOperations.DivideRounded32(left, right, mode)), Is.EqualTo(format.Divide(a, b, rounding)), $"{left:R} / {right:R} in {rounding}");
      }
    }
  }

  [Test]
  public void CompareAndClassifyHelpers_AgreeWithTheFormat() {
    foreach (var (left, right) in Pairs(1164).Take(3000)) {
      var (a, b) = (BitConverter.DoubleToUInt64Bits(left), BitConverter.DoubleToUInt64Bits(right));
      foreach (var predicate in Enum.GetValues<FloatPredicate>())
        Assert.That(HelperOperations.CompareFloat(left, right, (int)predicate), Is.EqualTo(IeeeFormats.Binary64.Compare(a, b, predicate)), $"{left:R} {predicate} {right:R}");

      Assert.That((FloatClass)HelperOperations.ClassifyFloat64(left), Is.EqualTo(IeeeFormats.Binary64.Classify(a)), $"{left:R}");
      Assert.That(HelperOperations.ClassifyFloat64(BitConverter.Int64BitsToDouble(0x7FF0000000000001L)), Is.EqualTo((int)FloatClass.SignalingNaN));
      var single = (float)left;
      Assert.That((FloatClass)HelperOperations.ClassifyFloat32(single), Is.EqualTo(IeeeFormats.Binary32.Classify(BitConverter.SingleToUInt32Bits(single))), $"{single:R}");
    }
  }

  // ---------------------------------------------------------------- the legalization

  private static IrModule Module(IrIntrinsic node, params IrLocal[] parameters)
    => new("M", [new IrFunction("F", true, node.Type, parameters, [], new IrBlock([new IrReturn(node)]))]);

  private static IrModule Legalized(IrModule module) => LibraryLegalization.Lower(module, () => HelperHarness.Library, FloatLegalization.Rewrite(_ => true));

  private static IEnumerable<TestCaseData> ArithmeticCases()
    => from op in new[] { IrIntrinsicOp.FloatAdd, IrIntrinsicOp.FloatSubtract, IrIntrinsicOp.FloatMultiply, IrIntrinsicOp.FloatDivide }
       from type in new[] { IrType.F32, IrType.F64 }
       from mode in Enum.GetValues<RoundingMode>()
       select new TestCaseData(op, type, mode).SetArgDisplayNames(op.ToString(), type.ToString(), IeeeNames.Of(mode));

  [TestCaseSource(nameof(ArithmeticCases))]
  public void LegalizedArithmetic_ComputesTheNode(IrIntrinsicOp op, IrType type, RoundingMode mode) {
    var a = new IrLocal("a", type);
    var b = new IrLocal("b", type);
    var module = Module(IeeeOperations.Arithmetic(op, new IrLocalRef(a), new IrLocalRef(b), mode), a, b);
    var legalized = Legalized(module);
    Assert.That(IrVerifier.Verify(legalized), Is.Empty);
    Assert.That(IrTree.Nodes(legalized.Functions[0].Body).OfType<IrIntrinsic>().Where(node => IeeeOperations.Is(node.Op)), Is.Empty);
    Assert.That(legalized.Implementations.Count == 0, Is.EqualTo(mode == RoundingMode.NearestEven), "nearest even is the operator of the IR");
    var random = new Random(116 + (int)mode);
    foreach (var (left, right) in _specials.Take(12).Zip(_specials.Skip(6).Concat(_specials)).Concat(Enumerable.Range(0, 40).Select(_ => (Random(random), Random(random))))) {
      object[] arguments = type == IrType.F32 ? [(float)left, (float)right] : [left, right];
      var expected = IrInterpreter.Call(module, "F", arguments)!;
      var actual = IrInterpreter.Call(legalized, "F", arguments)!;
      Assert.That(Canonical(actual), Is.EqualTo(Canonical(expected)), $"{op}({left:R}, {right:R}) in {mode}");
    }
  }

  private static ulong Canonical(object value) => value is float single ? Expected(single) : Expected((double)value);

  [Test]
  public void LegalizedComparisons_ComputeTheNode() {
    foreach (var type in new[] { IrType.F32, IrType.F64 })
      foreach (var predicate in Enum.GetValues<FloatPredicate>()) {
        var a = new IrLocal("a", type);
        var b = new IrLocal("b", type);
        var module = Module(IeeeOperations.Compare(predicate, new IrLocalRef(a), new IrLocalRef(b)), a, b);
        var legalized = Legalized(module);
        Assert.That(IrVerifier.Verify(legalized), Is.Empty);
        var direct = predicate is FloatPredicate.OrderedEqual or FloatPredicate.UnorderedNotEqual or FloatPredicate.OrderedLess
          or FloatPredicate.OrderedLessOrEqual or FloatPredicate.OrderedGreater or FloatPredicate.OrderedGreaterOrEqual;
        Assert.That(legalized.Implementations.Count == 0, Is.EqualTo(direct), $"{predicate} is an operator of the IR");
        foreach (var left in _specials.Take(10))
          foreach (var right in _specials.Take(10)) {
            object[] arguments = type == IrType.F32 ? [(float)left, (float)right] : [left, right];
            Assert.That(IrInterpreter.Call(legalized, "F", arguments), Is.EqualTo(IrInterpreter.Call(module, "F", arguments)), $"{left:R} {predicate} {right:R} ({type})");
          }
      }
  }

  [Test]
  public void LegalizedClassification_ComputesTheNode() {
    foreach (var type in new[] { IrType.F32, IrType.F64 }) {
      var a = new IrLocal("a", type);
      var module = Module(IeeeOperations.Classify(new IrLocalRef(a)), a);
      var legalized = Legalized(module);
      Assert.That(IrVerifier.Verify(legalized), Is.Empty);
      foreach (var value in _specials) {
        object[] arguments = type == IrType.F32 ? [(float)value] : [value];
        Assert.That(IrInterpreter.Call(legalized, "F", arguments), Is.EqualTo(IrInterpreter.Call(module, "F", arguments)), $"{value:R} ({type})");
      }
    }
  }

  /// <summary>A module of every operation, in a directed mode and with predicates no operator states.</summary>
  private static IrModule EveryOperation() {
    var a = new IrLocal("a", IrType.F64);
    var b = new IrLocal("b", IrType.F32);
    IrExpr x = new IrLocalRef(a);
    IrExpr y = new IrLocalRef(b);
    var sum = IeeeOperations.Arithmetic(IrIntrinsicOp.FloatAdd, x, IeeeOperations.Arithmetic(IrIntrinsicOp.FloatMultiply, x, x, RoundingMode.Up), RoundingMode.Down);
    var quotient = IeeeOperations.Arithmetic(IrIntrinsicOp.FloatDivide, y, IeeeOperations.Arithmetic(IrIntrinsicOp.FloatSubtract, y, y, RoundingMode.NearestAway), RoundingMode.TowardZero);
    var test = new IrBinary(IrBinaryOp.LogicalAnd, IeeeOperations.Compare(FloatPredicate.UnorderedLess, x, sum), IeeeOperations.Compare(FloatPredicate.OrderedNotEqual, y, quotient));
    var classes = new IrBinary(IrBinaryOp.Add, new IrConvert(IrType.I32, IeeeOperations.Classify(x)), new IrConvert(IrType.I32, IeeeOperations.Classify(y)));
    return new IrModule("M", [new IrFunction("F", true, IrType.I32, [a, b], [], new IrBlock([
      new IrReturn(new IrConditional(test, classes, new IrConst(IrType.I32, -1)))
    ]))]);
  }

  /// <summary>Every backend takes the operations through its legalization and writes the module: no backend writes them itself.</summary>
  [Test]
  public void EveryBackend_WritesTheLegalizedOperations() {
    var module = EveryOperation();
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    IrModule Checked(IrModule legalized, Func<IrModule, int> problems) {
      Assert.That(IrVerifier.Verify(legalized), Is.Empty);
      Assert.That(problems(legalized), Is.Zero);
      Assert.That(IrInterpreter.Call(legalized, "F", [2.5, 3.0f]), Is.EqualTo(IrInterpreter.Call(module, "F", [2.5, 3.0f])));
      return legalized;
    }

    foreach (var profile in new[] { CSharpProfile.Net20, CSharpProfile.Net100 }) {
      var legalized = Checked(CSharpEmitter.Legalize(module, () => HelperHarness.Library, profile, FloatMode.Accurate), m => CSharpEmitter.Check(m, profile).Count);
      Assert.That(string.Concat(CSharpEmitter.Emit(legalized, profile).Select(file => file.Content)), Does.Contain("AddRounded64"), profile.ToString());
    }

    foreach (var profile in new[] { JavaScriptProfile.Parse("es5"), JavaScriptProfile.Default }) {
      var legalized = Checked(JavaScriptEmitter.Legalize(module, () => HelperHarness.Library, profile, FloatMode.Accurate), m => JavaScriptEmitter.Check(m, profile).Count);
      Assert.That(string.Concat(JavaScriptEmitter.Emit(legalized, profile).Select(file => file.Content)), Does.Contain("DivideRounded32"), profile.ToString());
    }

    var python = Checked(PythonEmitter.Legalize(module, () => HelperHarness.Library, PythonProfile.Default, FloatMode.Accurate), m => PythonEmitter.Check(m, PythonProfile.Default).Count);
    Assert.That(string.Concat(PythonEmitter.Emit(python, PythonProfile.Default).Select(file => file.Content)), Does.Contain("compare_float").IgnoreCase.Or.Contain("CompareFloat"));
    var rust = Checked(RustEmitter.Legalize(module, () => HelperHarness.Library, RustProfile.Rust2021), m => RustEmitter.Check(m, RustProfile.Rust2021).Count);
    Assert.That(string.Concat(RustEmitter.Emit(rust, RustProfile.Rust2021).Select(file => file.Content)), Does.Contain("classify_float64").IgnoreCase.Or.Contain("ClassifyFloat64"));
  }

  [Test]
  public void OtherExpressions_AreLeftToTheOtherRewrites() {
    Assert.That(() => IeeeLegalization.Call(new IrConst(IrType.I32, 0), null!), Throws.ArgumentNullException);
    Assert.That(IeeeLegalization.Rewrite, Is.Not.Null);
    var x = new IrLocal("x", IrType.F64);
    var module = Module(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F64, [new IrLocalRef(x)]), x);
    Assert.That(LibraryLegalization.Lower(module, () => HelperHarness.Library, IeeeLegalization.Rewrite), Is.SameAs(module));
  }

}
