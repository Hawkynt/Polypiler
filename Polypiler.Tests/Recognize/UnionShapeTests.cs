using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Tests.Recognize;

/// <summary>The nodes of closed unions (#1582) in the shape helpers of recognition: operands, rebuilding, equality and which nodes may trap.</summary>
[TestFixture]
public sealed class UnionShapeTests {

  private static readonly IrType Number = IrType.Union(IrType.I32, IrType.String);

  [Test]
  public void TheShapeHelpers_SeeAndRebuildTheOperand() {
    var value = new IrUnionOf(Number, 0, new IrConst(IrType.I32, 1));
    var other = new IrUnionOf(Number, 0, new IrConst(IrType.I32, 2));
    IrExpr[] nodes = [value, new IrUnionTagOf(value), new IrUnionValue(value, 0) { IsTested = true }];
    foreach (var node in nodes) {
      var operands = IrShape.Operands(node);
      Assert.That(operands, Is.EqualTo(IrTree.Operands(node)));
      var rebuilt = IrShape.With(node, [.. operands.Select(operand => operand is IrConst ? new IrConst(IrType.I32, 2) : (IrExpr)other)]);
      Assert.That(rebuilt.GetType(), Is.EqualTo(node.GetType()));
      Assert.That(IrShape.Same(node, node), Is.True);
      Assert.That(IrShape.Same(node, rebuilt), Is.False);
      Assert.That(IrShape.IsSideEffectFreeNode(node), Is.True);
    }

    Assert.That(IrShape.IsTrapFreeNode(new IrUnionValue(value, 0)), Is.False);
    Assert.That(IrShape.IsTrapFreeNode(new IrUnionValue(value, 0) { IsTested = true }), Is.True);
    Assert.That(IrShape.IsTrapFreeNode(value) && IrShape.IsTrapFreeNode(new IrUnionTagOf(value)), Is.True);
  }

}
