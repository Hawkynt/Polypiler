using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Float;

/// <summary>The interpreter negates a number in its own format.</summary>
[TestFixture]
public sealed class IrFloatNegationTests {

  [TestCase(1.5f, -1.5f)]
  [TestCase(0.0f, -0.0f)]
  public void NegatedFloat_IsAFloat(float value, float negated) {
    var x = new IrLocal("x", IrType.F32);
    var module = new IrModule("M", [
      new IrFunction("F", true, IrType.F32, [x], [], new IrBlock([new IrReturn(new IrUnary(IrUnaryOp.Negate, new IrLocalRef(x)))]))
    ]);
    var result = IrInterpreter.Call(module, "F", [value]);
    Assert.That(result, Is.TypeOf<float>());
    Assert.That(System.BitConverter.SingleToInt32Bits((float)result!), Is.EqualTo(System.BitConverter.SingleToInt32Bits(negated)));
  }

}
