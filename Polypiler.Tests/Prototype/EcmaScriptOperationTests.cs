using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Prototype;

/// <summary>
/// The three operations the IR has for ECMA-262's numbers: <c>ToInt32</c>, <c>ToUint32</c> and
/// <c>Math.round</c>. Every expected value is what <c>node</c> printed for the same operand.
/// </summary>
[TestFixture]
public sealed class EcmaScriptOperationTests {

  private static readonly IrLocal _x = new("x", IrType.F64);
  private static readonly IrLocal _single = new("x", IrType.F32);

  private static IrModule Module(IrIntrinsicOp op, IrType result, IrLocal parameter)
    => new("m", [new IrFunction("f", true, result, [parameter], [], new IrBlock([new IrReturn(new IrIntrinsic(op, result, [new IrLocalRef(parameter)]))]))]);

  /// <summary>Test design 7: <c>ToInt32</c> at zero, at the halves, around 2^31, 2^32 and 2^53, far beyond, at the infinities and at NaN.</summary>
  [TestCase(0.0, 0)]
  [TestCase(-0.0, 0)]
  [TestCase(0.5, 0)]
  [TestCase(-0.5, 0)]
  [TestCase(1.0, 1)]
  [TestCase(-1.0, -1)]
  [TestCase(1.9, 1)]
  [TestCase(-1.9, -1)]
  [TestCase(2147483647.0, 2147483647)]
  [TestCase(2147483648.0, -2147483648)]
  [TestCase(-2147483648.0, -2147483648)]
  [TestCase(-2147483649.0, 2147483647)]
  [TestCase(4294967295.0, -1)]
  [TestCase(4294967296.0, 0)]
  [TestCase(4294967297.0, 1)]
  [TestCase(-4294967297.0, -1)]
  [TestCase(9007199254740992.0, 0)]
  [TestCase(9007199254740994.0, 2)]
  [TestCase(-9007199254740994.0, -2)]
  [TestCase(1e21, -559939584)]
  [TestCase(-1e21, 559939584)]
  [TestCase(1.7976931348623157e308, 0)]
  [TestCase(double.PositiveInfinity, 0)]
  [TestCase(double.NegativeInfinity, 0)]
  [TestCase(double.NaN, 0)]
  public void ToInt32_IsTheNumberModuloTwoToThe32(double value, int expected)
    => Assert.That(IrInterpreter.Call(Module(IrIntrinsicOp.ToInt32, IrType.I32, _x), "f", [value]), Is.EqualTo(expected));

  /// <summary>Test design 7: <c>ToUint32</c> at the same boundaries.</summary>
  [TestCase(0.0, 0u)]
  [TestCase(-0.0, 0u)]
  [TestCase(0.5, 0u)]
  [TestCase(-0.5, 0u)]
  [TestCase(1.0, 1u)]
  [TestCase(-1.0, 4294967295u)]
  [TestCase(2147483647.0, 2147483647u)]
  [TestCase(2147483648.0, 2147483648u)]
  [TestCase(4294967295.0, 4294967295u)]
  [TestCase(4294967296.0, 0u)]
  [TestCase(4294967297.0, 1u)]
  [TestCase(-4294967297.0, 4294967295u)]
  [TestCase(9007199254740992.0, 0u)]
  [TestCase(9007199254740994.0, 2u)]
  [TestCase(1e21, 3735027712u)]
  [TestCase(-1e21, 559939584u)]
  [TestCase(double.PositiveInfinity, 0u)]
  [TestCase(double.NegativeInfinity, 0u)]
  [TestCase(double.NaN, 0u)]
  public void ToUint32_IsTheNumberModuloTwoToThe32(double value, uint expected)
    => Assert.That(IrInterpreter.Call(Module(IrIntrinsicOp.ToUint32, IrType.U32, _x), "f", [value]), Is.EqualTo(expected));

  /// <summary>Test design 9: <c>Math.round</c> takes a tie upwards and keeps the sign of a zero.</summary>
  [TestCase(0.5, 1.0)]
  [TestCase(-0.5, -0.0)]
  [TestCase(1.5, 2.0)]
  [TestCase(-1.5, -1.0)]
  [TestCase(2.5, 3.0)]
  [TestCase(-2.5, -2.0)]
  [TestCase(-0.0, -0.0)]
  [TestCase(0.0, 0.0)]
  [TestCase(-0.2, -0.0)]
  [TestCase(0.2, 0.0)]
  [TestCase(-0.7, -1.0)]
  [TestCase(0.49999999999999994, 0.0)]
  [TestCase(-0.50000000000000011, -1.0)]
  [TestCase(4503599627370495.5, 4503599627370496.0)]
  [TestCase(4503599627370497.0, 4503599627370497.0)]
  [TestCase(-4503599627370495.5, -4503599627370495.0)]
  [TestCase(9007199254740993.0, 9007199254740992.0)]
  [TestCase(1.7976931348623157e308, 1.7976931348623157e308)]
  [TestCase(double.PositiveInfinity, double.PositiveInfinity)]
  [TestCase(double.NegativeInfinity, double.NegativeInfinity)]
  [TestCase(double.NaN, double.NaN)]
  public void RoundHalfUp_IsMathRound(double value, double expected) {
    var rounded = (double)IrInterpreter.Call(Module(IrIntrinsicOp.RoundHalfUp, IrType.F64, _x), "f", [value])!;
    Assert.That(BitConverter.DoubleToInt64Bits(rounded), Is.EqualTo(BitConverter.DoubleToInt64Bits(expected)));
  }

  /// <summary>A binary32 number rounds like the double it is.</summary>
  [TestCase(2.5f, 3f)]
  [TestCase(-2.5f, -2f)]
  [TestCase(-0.5f, -0f)]
  [TestCase(16777216f, 16777216f)]
  public void RoundHalfUp_OfABinary32Number(float value, float expected) {
    var rounded = (float)IrInterpreter.Call(Module(IrIntrinsicOp.RoundHalfUp, IrType.F32, _single), "f", [value])!;
    Assert.That(BitConverter.SingleToInt32Bits(rounded), Is.EqualTo(BitConverter.SingleToInt32Bits(expected)));
  }

  /// <summary>The verifier accepts the operations on the types they have and on no other.</summary>
  [Test]
  public void Verifier_KnowsTheTypesOfTheOperations() {
    Assert.That(IrVerifier.Verify(Module(IrIntrinsicOp.ToInt32, IrType.I32, _x)), Is.Empty);
    Assert.That(IrVerifier.Verify(Module(IrIntrinsicOp.ToUint32, IrType.U32, _x)), Is.Empty);
    Assert.That(IrVerifier.Verify(Module(IrIntrinsicOp.RoundHalfUp, IrType.F64, _x)), Is.Empty);
    Assert.That(IrVerifier.Verify(Module(IrIntrinsicOp.RoundHalfUp, IrType.F32, _single)), Is.Empty);

    Assert.That(IrVerifier.Verify(Module(IrIntrinsicOp.ToInt32, IrType.U32, _x)).Single().Message, Does.Contain("ToInt32 with the type u32 is applied to (f64)"));
    Assert.That(IrVerifier.Verify(Module(IrIntrinsicOp.ToInt32, IrType.I32, _single)).Single().Message, Does.Contain("ToInt32 with the type i32 is applied to (f32)"));
    Assert.That(IrVerifier.Verify(Module(IrIntrinsicOp.ToUint32, IrType.I32, _x)).Single().Message, Does.Contain("ToUint32 with the type i32 is applied to (f64)"));
    Assert.That(IrVerifier.Verify(Module(IrIntrinsicOp.ToUint32, IrType.U32, new IrLocal("x", IrType.I32))).Single().Message, Does.Contain("ToUint32 with the type u32 is applied to (i32)"));
    Assert.That(IrVerifier.Verify(Module(IrIntrinsicOp.RoundHalfUp, IrType.F64, _single)).Single().Message, Does.Contain("RoundHalfUp with the type f64 is applied to (f32)"));
  }

  /// <summary>JavaScript has the three operations itself, so the emitted module uses them as they are.</summary>
  [TestCase(IrIntrinsicOp.ToInt32, "return x | 0;")]
  [TestCase(IrIntrinsicOp.ToUint32, "return x >>> 0;")]
  [TestCase(IrIntrinsicOp.RoundHalfUp, "return Math.round(x);")]
  public void JavaScriptEmitter_WritesTheOperationItself(IrIntrinsicOp op, string expected) {
    var result = op == IrIntrinsicOp.ToInt32 ? IrType.I32 : op == IrIntrinsicOp.ToUint32 ? IrType.U32 : IrType.F64;
    Assert.That(JavaScriptEmitter.Emit(Module(op, result, _x)).Single().Content, Does.Contain(expected));
  }

  /// <summary>A coercion that is an operand is put in parentheses.</summary>
  [Test]
  public void JavaScriptEmitter_ParenthesizesACoercionThatIsAnOperand() {
    var coerced = new IrIntrinsic(IrIntrinsicOp.ToInt32, IrType.I32, [new IrLocalRef(_x)]);
    var module = new IrModule("m", [new IrFunction("f", true, IrType.I32, [_x], [], new IrBlock([new IrReturn(new IrBinary(IrBinaryOp.Xor, coerced, coerced))]))]);
    Assert.That(JavaScriptEmitter.Emit(module).Single().Content, Does.Contain("return (x | 0) ^ (x | 0);"));
  }

}
