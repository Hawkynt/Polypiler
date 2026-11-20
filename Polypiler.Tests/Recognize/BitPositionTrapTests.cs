using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Tests.Recognize;

/// <summary>A single-bit operation traps only on a position outside the width; with a position always inside it, it is total.</summary>
[TestFixture]
public sealed class BitPositionTrapTests {

  private static readonly IrExpr _value = new IrLocalRef(new IrLocal("x", IrType.U32));
  private static readonly IrExpr _n = new IrLocalRef(new IrLocal("n", IrType.I32));

  private static IrExpr Const(long value) => new IrConst(IrType.I32, value);

  [TestCase(0, true)]
  [TestCase(31, true)]
  [TestCase(32, false)]
  [TestCase(-1, false)]
  public void AConstantPosition_IsTotalOnlyWithinTheWidth(long position, bool total) {
    foreach (var op in new[] { IrIntrinsicOp.TestBit, IrIntrinsicOp.SetBit, IrIntrinsicOp.ClearBit, IrIntrinsicOp.ToggleBit })
      Assert.That(IrShape.IsTrapFree(new IrIntrinsic(op, op == IrIntrinsicOp.TestBit ? IrType.Bool : IrType.U32, [_value, Const(position)])), Is.EqualTo(total), op.ToString());
  }

  [TestCase(31, true, true)]
  [TestCase(31, false, true)]
  [TestCase(7, true, true)]
  [TestCase(32, true, false)]
  [TestCase(-1, true, false)]
  public void AMaskedPosition_IsTotalWhenTheMaskIsWithinTheWidth(long mask, bool maskOnTheRight, bool total) {
    var position = maskOnTheRight ? new IrBinary(IrBinaryOp.And, _n, Const(mask)) : new IrBinary(IrBinaryOp.And, Const(mask), _n);
    Assert.That(IrShape.IsTrapFree(new IrIntrinsic(IrIntrinsicOp.SetBit, IrType.U32, [_value, position])), Is.EqualTo(total));
  }

  [Test]
  public void AnyOtherPosition_MayTrap() {
    Assert.That(IrShape.IsTrapFree(new IrIntrinsic(IrIntrinsicOp.SetBit, IrType.U32, [_value, _n])), Is.False);
    Assert.That(IrShape.IsTrapFree(new IrIntrinsic(IrIntrinsicOp.SetBit, IrType.U32, [_value, new IrBinary(IrBinaryOp.Or, _n, Const(1))])), Is.False);
    Assert.That(IrShape.IsTrapFree(new IrIntrinsic(IrIntrinsicOp.SetBit, IrType.U32, [_value, new IrBinary(IrBinaryOp.And, _n, _n)])), Is.False);
  }

  [Test]
  public void AFloatingPointOperand_IsNotASingleBitOperation() {
    var d = new IrLocalRef(new IrLocal("d", IrType.F64));
    Assert.That(IrShape.IsTrapFree(new IrIntrinsic(IrIntrinsicOp.TestBit, IrType.Bool, [d, Const(0)])), Is.False);
  }
}
