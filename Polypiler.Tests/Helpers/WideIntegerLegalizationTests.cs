using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Tests.Helpers.Support;

namespace Polypiler.Tests.Helpers;

/// <summary>
/// Test design 2 of #1358: the wide-integer legalization O0075. Every operation on a 64-bit integer,
/// legalized into the calls of the helpers on two 32-bit limbs and then run through the passes,
/// computes in the interpreter what the operation computes - the value, or the class of the trap - for
/// the edges 0, ±1, 2^31, 2^32, 2^63 − 1 and −2^63 against each other and for random pairs (fixed
/// seed). What the legalized module keeps on 64 bits is only what a profile writes with the limbs.
/// </summary>
[TestFixture]
public sealed class WideIntegerLegalizationTests {

  private static readonly long[] _edges = [
    0, 1, -1, 2, -2, int.MaxValue, int.MinValue, (long)int.MaxValue + 1, (long)int.MinValue - 1, uint.MaxValue, (long)uint.MaxValue + 1,
    0x1_0000_0001, -0x1_0000_0000, long.MaxValue, long.MinValue, long.MaxValue - 1, long.MinValue + 1, 0x7FFF_FFFF_0000_0000, 3, 10, -10
  ];

  private static readonly IrBinaryOp[] _binary = [
    IrBinaryOp.Add, IrBinaryOp.Subtract, IrBinaryOp.Multiply, IrBinaryOp.Divide, IrBinaryOp.Remainder,
    IrBinaryOp.AddChecked, IrBinaryOp.SubtractChecked, IrBinaryOp.MultiplyChecked,
    IrBinaryOp.And, IrBinaryOp.Or, IrBinaryOp.Xor,
    IrBinaryOp.Equal, IrBinaryOp.NotEqual, IrBinaryOp.Less, IrBinaryOp.LessOrEqual, IrBinaryOp.Greater, IrBinaryOp.GreaterOrEqual
  ];

  private static IEnumerable<TestCaseData> Binaries()
    => from op in _binary
       from type in new[] { IrType.I64, IrType.U64 }
       select new TestCaseData(op, type).SetArgDisplayNames(op.ToString(), type.ToString());

  private static IEnumerable<TestCaseData> Shifts()
    => from op in new[] { IrBinaryOp.ShiftLeft, IrBinaryOp.ShiftRight, IrBinaryOp.ShiftRightLogical }
       from type in new[] { IrType.I64, IrType.U64 }
       select new TestCaseData(op, type).SetArgDisplayNames(op.ToString(), type.ToString());

  private static IEnumerable<TestCaseData> Unaries()
    => from op in new[] { IrUnaryOp.Negate, IrUnaryOp.NegateChecked, IrUnaryOp.BitNot }
       from type in new[] { IrType.I64, IrType.U64 }
       select new TestCaseData(op, type).SetArgDisplayNames(op.ToString(), type.ToString());

  private static readonly IrType[] _integers = [IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64];

  private static IEnumerable<TestCaseData> IntegerConversions()
    => from wide in new[] { IrType.I64, IrType.U64 }
       from other in _integers
       from toWide in new[] { true, false }
       from contract in new[] { IrConversion.Wrap, IrConversion.Trap }
       let source = toWide ? other : wide
       let target = toWide ? wide : other
       where source != target
       select new TestCaseData(source, target, contract).SetArgDisplayNames(source.ToString(), target.ToString(), contract.ToString());

  private static IEnumerable<TestCaseData> FloatConversions()
    => from wide in new[] { IrType.I64, IrType.U64 }
       from floating in new[] { IrType.F64, IrType.F32 }
       from (bool toFloat, IrConversion contract) way in new[] { (true, IrConversion.Wrap), (false, IrConversion.Saturate), (false, IrConversion.Trap) }
       select new TestCaseData(wide, floating, way.toFloat, way.contract).SetArgDisplayNames(wide.ToString(), floating.ToString(), way.toFloat ? "to float" : "from float", way.contract.ToString());

  [TestCaseSource(nameof(Binaries))]
  public void Operator_ComputesWhatTheOperationComputes(IrBinaryOp op, IrType type) {
    var (a, b) = (new IrLocal("a", type), new IrLocal("b", type));
    var node = new IrBinary(op, new IrLocalRef(a), new IrLocalRef(b));
    var module = Function(node.Type, [a, b], node);
    Agree(module, Pairs(type, type));
  }

  [TestCaseSource(nameof(Shifts))]
  public void Shift_ComputesWhatTheOperationComputes(IrBinaryOp op, IrType type) {
    var (a, count) = (new IrLocal("a", type), new IrLocal("count", IrType.I32));
    var module = Function(type, [a, count], new IrBinary(op, new IrLocalRef(a), new IrLocalRef(count)));
    var lists = _edges.SelectMany(value => Enumerable.Range(-2, 70).Select(shift => new object[] { HelperHarness.Host(type, value), shift })).ToList();
    Agree(module, lists);
  }

  [TestCaseSource(nameof(Unaries))]
  public void Unary_ComputesWhatTheOperationComputes(IrUnaryOp op, IrType type) {
    var a = new IrLocal("a", type);
    var module = Function(type, [a], new IrUnary(op, new IrLocalRef(a)));
    Agree(module, Values(type).Select(value => new[] { value }));
  }

  [TestCaseSource(nameof(IntegerConversions))]
  public void IntegerConversion_ComputesWhatTheOperationComputes(IrType from, IrType to, IrConversion contract) {
    var a = new IrLocal("a", from);
    var module = Function(to, [a], new IrConvert(to, new IrLocalRef(a), contract));
    Agree(module, Values(from).Select(value => new[] { value }));
  }

  [TestCaseSource(nameof(FloatConversions))]
  public void FloatConversion_ComputesWhatTheOperationComputes(IrType wide, IrType floating, bool toFloat, IrConversion contract) {
    var (from, to) = toFloat ? (wide, floating) : (floating, wide);
    var a = new IrLocal("a", from);
    var module = Function(to, [a], new IrConvert(to, new IrLocalRef(a), contract));
    var values = toFloat
      ? Values(from).Concat(new long[] { 0x0020_0000_0000_0001, 0x0020_0000_0000_0003, 0x7FFF_FF80_0000_0001, -0x0020_0000_0000_0001, 0x0000_0000_0100_0001 }.Select(value => HelperHarness.Host(from, value)))
      : Numbers().Select(number => from == IrType.F32 ? (object)(float)number : number);
    Agree(module, values.Select(value => new[] { value }));
  }

  [TestCase(IrTypeKind.I64)]
  [TestCase(IrTypeKind.U64)]
  public void GenericEqualityAndOrder_AreThoseOfTheNumbers(IrTypeKind kind) {
    var type = Wide(kind);
    var (a, b) = (new IrLocal("a", type), new IrLocal("b", type));
    Agree(Function(IrType.Bool, [a, b], new IrGenericEquals(new IrLocalRef(a), new IrLocalRef(b))), Pairs(type, type));
    Agree(Function(IrType.I32, [a, b], new IrGenericCompare(new IrLocalRef(a), new IrLocalRef(b))), Pairs(type, type));
  }

  /// <summary>A step of a variable and of an array element, as the value before or after, wrapping or trapping.</summary>
  [Test]
  public void Steps_AreThoseOfTheOperation([Values(IrTypeKind.I64, IrTypeKind.U64)] IrTypeKind kind, [Values] bool increment, [Values] bool postfix, [Values] bool trap) {
    var type = Wide(kind);
    var a = new IrLocal("a", type);
    var variable = Function(type, [a], new IrBinary(IrBinaryOp.Add, new IrStep(new IrLocalRef(a), increment, postfix, trap), new IrLocalRef(a)));
    Agree(variable, Values(type).Select(value => new[] { value }));

    var (array, index) = (new IrLocal("array", IrType.Array(type)), new IrLocal("index", IrType.I32));
    var element = new IrModule("M", [new IrFunction("F", true, type, [a, index], [array], new IrBlock([
      new IrDeclare(array, new IrNewArray(IrType.Array(type), new IrConst(IrType.I32, 2))),
      new IrStore(new IrLocalRef(array), new IrConst(IrType.I32, 1), new IrLocalRef(a)),
      new IrReturn(new IrBinary(IrBinaryOp.Add,
        new IrStep(new IrArrayLoad(new IrLocalRef(array), new IrLocalRef(index)), increment, postfix, trap),
        new IrArrayLoad(new IrLocalRef(array), new IrConst(IrType.I32, 1))))
    ]))]);
    Agree(element, Values(type).SelectMany(value => new[] { new object[] { value, 1 }, [value, 2], [value, -1] }));
  }

  /// <summary>An enum of 64 bits is compared, combined and tested for a flag on its underlying value; converting it changes no bit.</summary>
  [Test]
  public void EnumOf64Bits_IsComputedOnItsUnderlyingValue() {
    var module = Polypiler.Prototype.Testing.PrototypeFixture.Import("Snippet.cs", """
      public enum Big : ulong { One = 1, Half = 9223372036854775808, Max = ulong.MaxValue }
      public static class Snippet {
        public static int F(ulong v, ulong w) {
          var a = (Big)v;
          var b = (Big)w;
          var flags = (a | b) ^ ~(a & b);
          return (a == b ? 1 : 0) + (a != b ? 2 : 0) + (a.HasFlag(b) ? 4 : 0) + (flags == Big.Max ? 8 : 0) + ((ulong)a < (ulong)b ? 16 : 0);
        }
      }
      """);
    Agree(module, Pairs(IrType.U64, IrType.U64));
  }

  /// <summary>A step of an element of a view held in a variable is a lambda over the view; of a view that is no variable it is kept, for the backend to report.</summary>
  [Test]
  public void StepOfAViewElement_IsALambdaWhereTheViewIsAVariable([Values] bool postfix) {
    var (array, view, index) = (new IrLocal("array", IrType.Array(IrType.I64)), new IrLocal("view", IrType.Span(IrType.I64)), new IrLocal("index", IrType.I32));
    var step = new IrStep(new IrArrayLoad(new IrLocalRef(view), new IrLocalRef(index)), true, postfix, true);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I64, [array, index], [view], new IrBlock([
      new IrDeclare(view, new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(IrType.I64), [new IrLocalRef(array)])),
      new IrReturn(new IrBinary(IrBinaryOp.Add, step, new IrArrayLoad(new IrLocalRef(array), new IrConst(IrType.I32, 0))))
    ]))]);
    Agree(module, [.. Values(IrType.I64).Select(value => new object[] { new[] { (long)value, 7L }, 0 }), [new[] { 1L }, 1], [new[] { long.MaxValue }, 0]]);

    var computed = step with { Target = new IrArrayLoad(new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(IrType.I64), [new IrLocalRef(array)]), new IrLocalRef(index)) };
    var kept = new IrModule("M", [new IrFunction("F", true, IrType.I64, [array, index], [], new IrBlock([new IrReturn(computed)]))]);
    Assert.That(Legalized(kept), Is.SameAs(kept));
  }

  /// <summary>Legalization leaves no operation on 64 bits a profile without them cannot write with the limbs.</summary>
  [Test]
  public void Legalized_KeepsOnlyWhatTheLimbsWrite() {
    var (a, b) = (new IrLocal("a", IrType.I64), new IrLocal("b", IrType.U64));
    var module = Function(IrType.F64, [a, b], new IrBinary(IrBinaryOp.Add,
      new IrConvert(IrType.F64, new IrBinary(IrBinaryOp.Divide, new IrLocalRef(a), new IrConvert(IrType.I64, new IrLocalRef(b), IrConversion.Trap))),
      new IrConvert(IrType.F64, new IrBinary(IrBinaryOp.Multiply, new IrLocalRef(b), new IrConvert(IrType.U64, new IrConst(IrType.I32, -3))))));
    var legalized = Legalized(module);
    Assert.That(IrVerifier.Verify(legalized), Is.Empty);
    var left = legalized.Functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrExpr>().Where(IsWideOperation);
    Assert.That(left, Is.Empty);
  }

  /// <summary>Whether a node is an operation on 64 bits that is not written with the limbs.</summary>
  private static bool IsWideOperation(IrExpr node) => node switch {
    IrBinary binary => binary.Left.Type.IsLong,
    IrUnary unary => unary.Type.IsLong,
    IrConvert conversion when conversion.Type.IsLong && conversion.Operand.Type.IsLong => conversion.Contract == IrConversion.Trap,
    IrConvert conversion when conversion.Operand.Type.IsLong => conversion.Type.IsFloat || conversion.Contract == IrConversion.Trap,
    IrConvert conversion => conversion.Type.IsLong,
    IrStep step => step.Type.IsLong,
    IrGenericEquals equality => equality.Left.Type.IsLong,
    IrGenericCompare order => order.Left.Type.IsLong,
    _ => false
  };

  private static IrType Wide(IrTypeKind kind) => kind == IrTypeKind.I64 ? IrType.I64 : IrType.U64;

  [Test]
  public void Call_WithoutCalls_Throws()
    => Assert.That(() => WideIntegerLibrary.Call(new IrConst(IrType.I64, 0), null!), Throws.ArgumentNullException);

  [Test]
  public void OtherNodes_AreKept() {
    Assert.Multiple(() => {
      Assert.That(LegalizeOne(new IrConst(IrType.I64, 5)), Is.Null);
      Assert.That(LegalizeOne(new IrBinary(IrBinaryOp.Add, new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 2))), Is.Null);
      Assert.That(LegalizeOne(new IrConvert(IrType.U32, new IrConst(IrType.I64, 5))), Is.Null, "the low limb");
      Assert.That(LegalizeOne(new IrConvert(IrType.U64, new IrConst(IrType.I64, 5))), Is.Null, "the change of signedness");
    });
  }

  /// <summary>Whether the rewrite of O0075 replaces a node, asked without a library.</summary>
  private static IrExpr? LegalizeOne(IrExpr node) {
    var module = Function(node.Type, [], node);
    var legalized = LibraryLegalization.Lower(module, () => throw new InvalidOperationException("no library"), WideIntegerLibrary.Call);
    return ReferenceEquals(legalized, module) ? null : node;
  }

  private static IrModule Function(IrType result, IReadOnlyList<IrLocal> parameters, IrExpr value)
    => new("M", [new IrFunction("F", true, result, parameters, [], new IrBlock([new IrReturn(value)]))]);

  private static IrModule Legalized(IrModule module) => LibraryLegalization.Lower(module, () => HelperHarness.Library, WideIntegerLibrary.Call);

  /// <summary>The module, its legalization and that after the passes give the same outcome for every argument list.</summary>
  private static void Agree(IrModule module, IEnumerable<object[]> lists) {
    var legalized = Legalized(module);
    Assert.That(IrVerifier.Verify(legalized), Is.Empty);
    var optimized = HelperHarness.Optimized(legalized);
    foreach (var list in lists) {
      var expected = HelperHarness.Outcome(module, list);
      Assert.That(HelperHarness.Outcome(legalized, list), Is.EqualTo(expected), $"legalized, F({string.Join(", ", list)})");
      Assert.That(HelperHarness.Outcome(optimized, list), Is.EqualTo(expected), $"optimized, F({string.Join(", ", list)})");
    }
  }

  private static IEnumerable<object> Values(IrType type) {
    var random = new Random(1358);
    return _edges.Concat(Enumerable.Range(0, 64).Select(_ => random.NextInt64(long.MinValue, long.MaxValue)))
      .Select(value => HelperHarness.Host(type, IrOperators.Wrap(type, value))).Distinct();
  }

  /// <summary>Every edge against every edge, and random pairs.</summary>
  private static List<object[]> Pairs(IrType left, IrType right) {
    var random = new Random(1358);
    var lists = (from x in _edges from y in _edges select new[] { HelperHarness.Host(left, x), HelperHarness.Host(right, y) }).ToList();
    for (var index = 0; index < 400; ++index) {
      var x = random.Next(4) == 0 ? random.NextInt64(-100000, 100000) : random.NextInt64(long.MinValue, long.MaxValue);
      var y = random.Next(4) == 0 ? random.NextInt64(-100000, 100000) : random.NextInt64(long.MinValue, long.MaxValue);
      lists.Add([HelperHarness.Host(left, x), HelperHarness.Host(right, y)]);
    }

    return lists;
  }

  private static IEnumerable<double> Numbers() => [
    0.0, -0.0, 0.5, -0.5, 1.5, -1.5, 3.7, -3.7, 1e16, -1e16, 1e300, -1e300, 4294967296.5, -4294967296.5, 2147483647.5, -2147483648.5,
    9.2233720368547758e18, -9.2233720368547758e18, 9.2233720368547748e18, -9.2233720368547748e18, 1.8446744073709552e19, 1.8446744073709550e19,
    double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon, -double.Epsilon, 123456789012345.75, -98765432109876.25, 4503599627370497.0
  ];

}
