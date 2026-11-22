using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Tests.Recognize;

/// <summary>A load from a constant table is total exactly when its index cannot reach past the table's end.</summary>
[TestFixture]
public sealed class TableReadTrapTests {

  private static readonly IrModuleConst _table = new("T", IrType.Array(IrType.U8), [.. Enumerable.Range(0, 32).Select(entry => (long)entry)]);
  private static readonly IrExpr _x = new IrLocalRef(new IrLocal("x", IrType.U32));
  private static readonly IrExpr _signed = new IrLocalRef(new IrLocal("s", IrType.I32));

  private static IrArrayLoad Load(IrExpr index) => new(new IrConstRef(_table), index);

  private static IrExpr Shift(IrExpr value, int count, IrBinaryOp op = IrBinaryOp.ShiftRight) => new IrBinary(op, value, new IrConst(IrType.I32, count));

  [TestCase(27, true)]
  [TestCase(28, true)]
  [TestCase(26, false)]
  [TestCase(0, false)]
  [TestCase(32, false)]
  public void AnUnsignedShiftedIndex_IsBoundedByItsRemainingBits(int count, bool total)
    => Assert.That(IrShape.IsTrapFree(Load(Shift(_x, count))), Is.EqualTo(total));

  [Test]
  public void ASignedShift_KeepsTheSignAndIsUnbounded() {
    Assert.That(IrShape.IsTrapFree(Load(Shift(_signed, 27))), Is.False);
    Assert.That(IrShape.IsTrapFree(Load(Shift(_signed, 27, IrBinaryOp.ShiftRightLogical))), Is.True);
  }

  [TestCase(31, true, true)]
  [TestCase(31, false, true)]
  [TestCase(32, true, false)]
  [TestCase(-1, true, false)]
  public void AMaskedIndex_IsBoundedByTheMask(long mask, bool maskOnTheRight, bool total) {
    var constant = new IrConst(IrType.I32, mask);
    var index = maskOnTheRight ? new IrBinary(IrBinaryOp.And, _signed, constant) : new IrBinary(IrBinaryOp.And, constant, _signed);
    Assert.That(IrShape.IsTrapFree(Load(index)), Is.EqualTo(total));
  }

  [TestCase(0, true)]
  [TestCase(31, true)]
  [TestCase(32, false)]
  [TestCase(-1, false)]
  public void AConstantIndex_IsInsideOrNot(long index, bool total)
    => Assert.That(IrShape.IsTrapFree(Load(new IrConst(IrType.I32, index))), Is.EqualTo(total));

  [Test]
  public void AConversion_KeepsTheBoundOnlyBelowItsSignBit() {
    var wide = new IrLocalRef(new IrLocal("w", IrType.U64));
    Assert.That(IrShape.IsTrapFree(Load(new IrConvert(IrType.I32, Shift(wide, 59)))), Is.True);
    Assert.That(IrShape.IsTrapFree(Load(new IrConvert(IrType.I8, Shift(_x, 24)))), Is.False, "255 does not fit below the sign of an i8");
  }

  [Test]
  public void TheIndexItself_MustBeTotal() {
    var array = new IrLocalRef(new IrLocal("a", IrType.Array(IrType.U32)));
    var loaded = new IrArrayLoad(array, new IrConst(IrType.I32, 0));
    Assert.That(IrShape.IsTrapFree(Load(Shift(loaded, 27))), Is.False);
  }

  [Test]
  public void ATableThatMayChange_IsNotATableRead() {
    var array = new IrLocalRef(new IrLocal("a", IrType.Array(IrType.U8)));
    Assert.That(IrShape.IsTrapFree(new IrArrayLoad(array, Shift(_x, 27))), Is.False);
    Assert.That(IrShape.IsTrapFree(new IrConstRef(_table)), Is.False, "the table alone is still no value a rule may duplicate");
  }
}
