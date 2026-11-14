using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Float;

/// <summary>
/// The legalization replaces the floating-point operations a target lacks by calls of their portable
/// implementations, copied into the module under fresh names with everything they need, and the result
/// computes the same bits as the intrinsics.
/// </summary>
[TestFixture]
public sealed class FloatLegalizationTests {

  private static readonly IrLocal _x = new("x", IrType.F64);
  private static readonly IrLocal _y = new("y", IrType.F32);

  /// <summary><c>F(x, y)</c> returns <c>Exp(x) + (double)Hypot(y, y)</c>; a constant and a function of the module are named as helpers would be.</summary>
  private static IrModule Module() {
    var exp = new IrIntrinsic(IrIntrinsicOp.Exp, IrType.F64, [new IrLocalRef(_x)]);
    var hypot = new IrIntrinsic(IrIntrinsicOp.Hypot, IrType.F32, [new IrLocalRef(_y), new IrLocalRef(_y)]);
    var body = new IrReturn(new IrBinary(IrBinaryOp.Add, exp, new IrConvert(IrType.F64, hypot)));
    return new IrModule("M", [
      new IrFunction("F", true, IrType.F64, [_x, _y], [], new IrBlock([body])),
      new IrFunction("FloatExp", false, IrType.F64, [_x], [], new IrBlock([new IrReturn(new IrLocalRef(_x))]))
    ], [new IrModuleConst("FloatExpTable", IrType.F64, [0])]);
  }

  [Test]
  public void EveryOperationTheTargetLacks_BecomesACallOfItsImplementation() {
    var lowered = FloatLegalization.Lower(Module(), FloatImplementations.Module, _ => false);
    Assert.That(IrVerifier.Verify(lowered), Is.Empty);
    var names = lowered.Functions.Select(function => function.Name).ToList();
    Assert.That(names, Does.Contain("FloatExp_").And.Contain("FloatHypotF"), "a name the module has is not taken twice");
    Assert.That(lowered.Functions.Skip(2).All(function => !function.IsPublic), Is.True);
    Assert.That(lowered.Constants.Select(constant => constant.Name), Is.Unique);

    foreach (var (x, y) in new[] { (0.5, 3.0f), (-700.0, 4.0f), (double.NaN, float.PositiveInfinity) }) {
      var expected = FloatOperations.Exp(x) + FloatOperations.HypotF(y, y);
      var actual = (double)IrInterpreter.Call(lowered, "F", [x, y])!;
      Assert.That(BitConverter.DoubleToInt64Bits(actual), Is.EqualTo(BitConverter.DoubleToInt64Bits(expected)));
    }
  }

  [Test]
  public void OperationsTheTargetHas_AreLeftAlone() {
    var module = Module();
    Assert.That(FloatLegalization.Lower(module, FloatImplementations.Module, _ => true), Is.SameAs(module));
  }

  [Test]
  public void ImplementationsOfAnImplementation_AreLegalizedAsWell() {
    // Sin of double calls the fused multiply-add; a target without it gets the implementation of that, too.
    var x = new IrLocal("x", IrType.F64);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.F64, [x], [], new IrBlock([
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sin, IrType.F64, [new IrLocalRef(x)]))
    ]))]);
    var lowered = FloatLegalization.Lower(module, FloatImplementations.Module, _ => false);
    Assert.That(lowered.Functions.Select(function => function.Name), Does.Contain("FloatFusedMultiplyAdd"));
    Assert.That(IrVerifier.Verify(lowered), Is.Empty);
    Assert.That(IrInterpreter.Call(lowered, "F", [1.0]), Is.EqualTo(FloatOperations.Sin(1.0)));
  }

  [Test]
  public void OperationsThatTrap_AndIntegerOperations_StayIntrinsics() {
    var x = new IrLocal("x", IrType.F64);
    var n = new IrLocal("n", IrType.U32);
    var clamp = new IrIntrinsic(IrIntrinsicOp.Clamp, IrType.F64, [new IrLocalRef(x), new IrLocalRef(x), new IrLocalRef(x)]);
    var log = new IrIntrinsic(IrIntrinsicOp.Log2, IrType.I32, [new IrLocalRef(n)]);
    Assert.That(FloatLegalization.IsReplaceable(clamp), Is.False);
    Assert.That(FloatLegalization.IsReplaceable(log), Is.False);
    Assert.That(FloatLegalization.IsReplaceable(new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F64, [new IrLocalRef(x)])), Is.False, "an operation every target has");
    Assert.That(FloatLegalization.IsReplaceable(new IrIntrinsic(IrIntrinsicOp.CopySign, IrType.F64, [new IrLocalRef(x), new IrLocalRef(x)])), Is.True);
  }

  [Test]
  public void AMissingImplementation_IsReported() {
    var empty = new IrModule("FloatOperations", []);
    Assert.That(() => FloatLegalization.Lower(Module(), empty, _ => false), Throws.ArgumentException.With.Message.Contains("'Exp'"));
  }

  [Test]
  public void EveryStatementAndExpression_IsCarriedThrough() {
    var x = new IrLocal("x", IrType.F64);
    var i = new IrLocal("i", IrType.I32);
    var a = new IrLocal("a", IrType.Array(IrType.F64));
    var sin = new IrIntrinsic(IrIntrinsicOp.Sin, IrType.F64, [new IrLocalRef(x)]);
    var body = new IrBlock([
      new IrDeclare(i, new IrConst(IrType.I32, 0)),
      new IrDeclare(a, new IrNewArray(IrType.Array(IrType.F64), new IrConst(IrType.I32, 2))),
      new IrStore(new IrLocalRef(a), new IrLocalRef(i), sin),
      new IrAssign(x, new IrUnary(IrUnaryOp.Negate, new IrArrayLoad(new IrLocalRef(a), new IrConst(IrType.I32, 0)))),
      new IrIf(new IrBinary(IrBinaryOp.Less, new IrLocalRef(x), sin), new IrBlock([new IrExprStmt(new IrStep(new IrLocalRef(i), true, false, false))]), null),
      new IrIf(new IrBinary(IrBinaryOp.Less, new IrLocalRef(x), sin), new IrBlock([]), new IrBlock([new IrExprStmt(sin)])),
      new IrWhile(new IrBinary(IrBinaryOp.Less, new IrLocalRef(i), new IrArrayLength(new IrLocalRef(a))), new IrBlock([new IrAssign(i, new IrBinary(IrBinaryOp.Add, new IrLocalRef(i), new IrConst(IrType.I32, 1))), new IrContinue()])),
      new IrDoWhile(new IrBlock([]), new IrBinary(IrBinaryOp.Equal, sin, new IrConvert(IrType.F64, new IrLocalRef(i)))),
      new IrFor(new IrConst(IrType.Bool, 0), new IrBlock([]), new IrBlock([])),
      new IrSwitch(new IrLocalRef(i), [new IrSwitchSection([1], true, new IrBlock([new IrAssign(x, new IrConditional(new IrConst(IrType.Bool, 1), sin, sin))]))]),
      new IrReturn(null)
    ]);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.Void, [x], [i, a], body)]);
    var lowered = FloatLegalization.Lower(module, FloatImplementations.Module, _ => false);
    Assert.That(IrVerifier.Verify(lowered), Is.Empty);
    var text = lowered.Functions[0].Body.ToString();
    Assert.That(lowered.Functions[0].Body.Statements, Has.Count.EqualTo(body.Statements.Count));
    Assert.That(IrInterpreter.Call(lowered, "F", [0.5]), Is.Null);
    Assert.That(text, Is.Not.Empty);
  }

}
