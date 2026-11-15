using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Tests.Helpers.Support;

namespace Polypiler.Tests.Helpers;

/// <summary>
/// Test design 4 of #1358: the traps of integer arithmetic as helpers of the library. The division and
/// the remainder, the checked sum, difference, product and negation, the checked conversions between
/// integers and the conversions of a floating-point number to an integer, legalized into the calls of
/// their helpers and run through the passes, end in the interpreter with the value or the trap the
/// operation has - division by zero, the overflow of a checked operation, the smallest value divided
/// by -1 - for the edges of every type against each other and random pairs (fixed seed).
/// </summary>
[TestFixture]
public sealed class TrapLegalizationTests {

  private static readonly IrType[] _numbers = [IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32];

  private static IEnumerable<TestCaseData> Divisions()
    => from op in new[] { IrBinaryOp.Divide, IrBinaryOp.Remainder }
       from type in _numbers
       select new TestCaseData(op, type).SetArgDisplayNames(op.ToString(), type.ToString());

  private static IEnumerable<TestCaseData> CheckedOperations()
    => from op in new[] { IrBinaryOp.AddChecked, IrBinaryOp.SubtractChecked, IrBinaryOp.MultiplyChecked }
       from type in new[] { IrType.I32, IrType.U32, IrType.I64, IrType.U64 }
       select new TestCaseData(op, type).SetArgDisplayNames(op.ToString(), type.ToString());

  private static IEnumerable<TestCaseData> CheckedConversions()
    => from source in new[] { IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64 }
       from target in new[] { IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64 }
       where source != target
       select new TestCaseData(source, target).SetArgDisplayNames(source.ToString(), target.ToString());

  private static IEnumerable<TestCaseData> FloatConversions()
    => from floating in new[] { IrType.F64, IrType.F32 }
       from to in _numbers
       from contract in new[] { IrConversion.Saturate, IrConversion.Trap }
       select new TestCaseData(floating, to, contract).SetArgDisplayNames(floating.ToString(), to.ToString(), contract.ToString());

  [TestCaseSource(nameof(Divisions))]
  public void Division_TrapsAsTheOperation(IrBinaryOp op, IrType type) {
    var (a, b) = (new IrLocal("a", type), new IrLocal("b", type));
    Agree(Function(type, [a, b], new IrBinary(op, new IrLocalRef(a), new IrLocalRef(b))), Pairs(type));
  }

  [TestCaseSource(nameof(CheckedOperations))]
  public void CheckedOperation_TrapsAsTheOperation(IrBinaryOp op, IrType type) {
    var (a, b) = (new IrLocal("a", type), new IrLocal("b", type));
    Agree(Function(type, [a, b], new IrBinary(op, new IrLocalRef(a), new IrLocalRef(b))), Pairs(type));
  }

  [TestCase(IrTypeKind.I32)]
  [TestCase(IrTypeKind.I64)]
  public void CheckedNegation_TrapsOnTheSmallestValue(IrTypeKind kind) {
    var type = kind == IrTypeKind.I32 ? IrType.I32 : IrType.I64;
    var a = new IrLocal("a", type);
    Agree(Function(type, [a], new IrUnary(IrUnaryOp.NegateChecked, new IrLocalRef(a))), Values(type).Select(value => new[] { value }));
  }

  [TestCaseSource(nameof(CheckedConversions))]
  public void CheckedConversion_TrapsOutsideTheTarget(IrType from, IrType to) {
    var a = new IrLocal("a", from);
    Agree(Function(to, [a], new IrConvert(to, new IrLocalRef(a), IrConversion.Trap)), Values(from).Select(value => new[] { value }));
  }

  [TestCaseSource(nameof(FloatConversions))]
  public void FloatConversion_SaturatesOrTraps(IrType floating, IrType to, IrConversion contract) {
    var a = new IrLocal("a", floating);
    var numbers = new[] {
      0.0, -0.0, 0.5, -0.5, 1.5, -1.5, 127.5, -128.5, 128.0, -129.0, 255.5, 256.0, 32767.5, -32768.5, 65535.5, 65536.0, 2147483647.5, -2147483648.5,
      2147483648.0, -2147483649.0, 4294967295.5, 4294967296.0, 1e300, -1e300, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon
    };
    Agree(Function(to, [a], new IrConvert(to, new IrLocalRef(a), contract)), numbers.Select(number => new[] { floating == IrType.F32 ? (object)(float)number : number }));
  }

  /// <summary>A checked step of a variable whose value is used is the lambda that stores the checked sum; of an array element it stays the target's.</summary>
  [Test]
  public void CheckedStep_OfAVariable_TrapsAsTheOperation([Values(IrTypeKind.I32, IrTypeKind.U32)] IrTypeKind kind, [Values] bool increment, [Values] bool postfix) {
    var type = kind == IrTypeKind.I32 ? IrType.I32 : IrType.U32;
    var a = new IrLocal("a", type);
    var step = new IrStep(new IrLocalRef(a), increment, postfix, true);
    Agree(Function(type, [a], new IrBinary(IrBinaryOp.Add, step, new IrLocalRef(a))), Values(type).Select(value => new[] { value }));

    var array = new IrLocal("x", IrType.Array(type));
    var element = Function(type, [array], new IrStep(new IrArrayLoad(new IrLocalRef(array), new IrConst(IrType.I32, 0)), increment, postfix, true));
    Assert.That(Legalized(element), Is.SameAs(element));
  }

  /// <summary>A constant divisor other than 0 and -1 leaves no check once the passes ran: the quotient of the target is all that is left.</summary>
  [Test]
  public void ConstantDivisor_LeavesNoCheck() {
    var a = new IrLocal("a", IrType.I32);
    var module = Function(IrType.I32, [a], new IrBinary(IrBinaryOp.Divide, new IrLocalRef(a), new IrConst(IrType.I32, 3)));
    var optimized = HelperHarness.Optimized(Legalized(module));
    Assert.That(optimized.Functions.Select(function => function.Name), Is.EqualTo(new[] { "F" }));
    Assert.That(IrPassSupportNodes(optimized).OfType<IrThrow>(), Is.Empty);
    Assert.That(IrPassSupportNodes(optimized).OfType<IrIf>(), Is.Empty);
  }

  [Test]
  public void Call_WithoutCalls_Throws()
    => Assert.That(() => TrapLibrary.Call(new IrConst(IrType.I32, 0), null!), Throws.ArgumentNullException);

  /// <summary>An operation that does not trap, a conversion of an enum and a division of 64 bits, which the profile has, stay as they are.</summary>
  [Test]
  public void OperationsThatDoNotTrap_AreKept() {
    var (a, b) = (new IrLocal("a", IrType.I32), new IrLocal("b", IrType.I64));
    foreach (var node in new IrExpr[] {
      new IrBinary(IrBinaryOp.Add, new IrLocalRef(a), new IrLocalRef(a)),
      new IrBinary(IrBinaryOp.Divide, new IrLocalRef(b), new IrLocalRef(b)),
      new IrConvert(IrType.U8, new IrLocalRef(a)),
      new IrUnary(IrUnaryOp.Negate, new IrLocalRef(a))
    }) {
      var module = Function(node.Type, [a, b], node);
      Assert.That(Legalized(module), Is.SameAs(module), node.ToString());
    }
  }

  private static IEnumerable<IrStmt> IrPassSupportNodes(IrModule module) => module.Functions.SelectMany(function => function.Body.Statements.SelectMany(Polypiler.Core.Recognize.IrStatements.All));

  private static IrModule Function(IrType result, IReadOnlyList<IrLocal> parameters, IrExpr value)
    => new("M", [new IrFunction("F", true, result, parameters, [], new IrBlock([new IrReturn(value)]))]);

  private static IrModule Legalized(IrModule module) => LibraryLegalization.Lower(module, () => HelperHarness.Library, TrapLibrary.Call);

  private static void Agree(IrModule module, IEnumerable<object[]> lists) {
    var legalized = Legalized(module);
    Assert.That(IrVerifier.Verify(legalized), Is.Empty);
    Assert.That(legalized, Is.Not.SameAs(module), "the operation is a helper of the library");
    var optimized = HelperHarness.Optimized(legalized);
    foreach (var list in lists) {
      var expected = HelperHarness.Outcome(module, list);
      Assert.That(HelperHarness.Outcome(legalized, list), Is.EqualTo(expected), $"legalized, F({string.Join(", ", list)})");
      Assert.That(HelperHarness.Outcome(optimized, list), Is.EqualTo(expected), $"optimized, F({string.Join(", ", list)})");
    }
  }

  private static IEnumerable<object> Values(IrType type) {
    var random = new Random(1358);
    long[] edges = [0, 1, -1, 2, -2, 127, -128, 255, 256, 32767, -32768, 65535, int.MaxValue, int.MinValue, uint.MaxValue, long.MaxValue, long.MinValue, 3, 7];
    return edges.Concat(Enumerable.Range(0, 48).Select(_ => random.Next(3) == 0 ? random.NextInt64(-1000, 1000) : random.NextInt64(long.MinValue, long.MaxValue)))
      .Select(value => HelperHarness.Host(type, IrOperators.Wrap(type, value))).Distinct();
  }

  private static List<object[]> Pairs(IrType type) {
    var values = Values(type).ToList();
    var random = new Random(1358);
    return [.. from x in values.Take(19) from y in values.Take(19) select new[] { x, y },
      .. Enumerable.Range(0, 300).Select(_ => new[] { values[random.Next(values.Count)], values[random.Next(values.Count)] })];
  }

}
