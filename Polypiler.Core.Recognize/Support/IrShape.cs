using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize;

/// <summary>
/// What a rule has to know about an expression before it may move, merge or drop it: whether two
/// expressions are the same tree, whether evaluating one changes anything, and whether it can trap.
/// </summary>
public static class IrShape {

  /// <summary>Compares expressions by their trees, which the records do not do for their argument lists.</summary>
  public static IEqualityComparer<IrExpr> Comparer { get; } = new TreeComparer();

  /// <summary>Whether two expressions are the same tree: the same nodes with the same operands.</summary>
  /// <param name="left">One expression.</param>
  /// <param name="right">The other.</param>
  /// <returns><see langword="true"/> when they are equal node by node.</returns>
  public static bool Same(IrExpr left, IrExpr right) {
    if (ReferenceEquals(left, right))
      return true;

    if (left.GetType() != right.GetType() || left.Type != right.Type)
      return false;

    return (left, right) switch {
      (IrConst a, IrConst b) => a.Value == b.Value,
      (IrLocalRef a, IrLocalRef b) => a.Local == b.Local,
      (IrConstRef a, IrConstRef b) => ReferenceEquals(a.Constant, b.Constant),
      (IrUnary a, IrUnary b) => a.Op == b.Op && Same(a.Operand, b.Operand),
      (IrBinary a, IrBinary b) => a.Op == b.Op && Same(a.Left, b.Left) && Same(a.Right, b.Right),
      (IrConvert a, IrConvert b) => a.Contract == b.Contract && Same(a.Operand, b.Operand),
      (IrConditional a, IrConditional b) => Same(a.Condition, b.Condition) && Same(a.WhenTrue, b.WhenTrue) && Same(a.WhenFalse, b.WhenFalse),
      (IrArrayLoad a, IrArrayLoad b) => Same(a.Array, b.Array) && Same(a.Index, b.Index),
      (IrArrayLength a, IrArrayLength b) => Same(a.Array, b.Array),
      (IrNewArray a, IrNewArray b) => Same(a.Length, b.Length),
      (IrCall a, IrCall b) => a.Function == b.Function && All(a.Arguments, b.Arguments),
      (IrIntrinsic a, IrIntrinsic b) => a.Op == b.Op && All(a.Arguments, b.Arguments),
      (IrStep a, IrStep b) => a.Increment == b.Increment && a.Postfix == b.Postfix && a.Trap == b.Trap && Same(a.Target, b.Target),
      _ => false
    };
  }

  private static bool All(IReadOnlyList<IrExpr> left, IReadOnlyList<IrExpr> right)
    => left.Count == right.Count && left.Zip(right).All(pair => Same(pair.First, pair.Second));

  /// <summary>The direct operands of an expression, in the order they are evaluated.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>Its operands; empty for a leaf.</returns>
  public static IReadOnlyList<IrExpr> Operands(IrExpr expression) => expression switch {
    IrUnary unary => [unary.Operand],
    IrBinary binary => [binary.Left, binary.Right],
    IrConvert conversion => [conversion.Operand],
    IrConditional choice => [choice.Condition, choice.WhenTrue, choice.WhenFalse],
    IrArrayLoad load => [load.Array, load.Index],
    IrArrayLength length => [length.Array],
    IrNewArray creation => [creation.Length],
    IrCall call => call.Arguments,
    IrIntrinsic intrinsic => intrinsic.Arguments,
    IrStep step => [step.Target],
    _ => []
  };

  /// <summary>Every node of an expression, the expression first.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The nodes in pre-order.</returns>
  public static IEnumerable<IrExpr> Nodes(IrExpr expression) {
    yield return expression;
    foreach (var node in Operands(expression).SelectMany(Nodes))
      yield return node;
  }

  /// <summary>
  /// Whether evaluating an expression changes nothing but may trap: it calls no function, steps no
  /// variable, allocates nothing and writes no array. Evaluated twice it yields the same value or the
  /// same trap, and evaluated once fewer it is missed by nobody.
  /// </summary>
  /// <param name="expression">The expression.</param>
  /// <returns><see langword="true"/> when it has no effect.</returns>
  public static bool IsSideEffectFree(IrExpr expression) => Nodes(expression).All(node => node switch {
    IrCall or IrStep or IrNewArray => false,
    IrIntrinsic intrinsic => intrinsic.Type != IrType.Void && intrinsic.Op != IrIntrinsicOp.ArrayOf,
    _ => true
  });

  /// <summary>
  /// Whether an expression has no effect and never traps: constants, variables, the arithmetic that
  /// wraps around, comparisons, conversions between integers that wrap, and the operations of the
  /// IR that are defined on every integer. It may be evaluated any number of times, or not at all.
  /// </summary>
  /// <param name="expression">The expression.</param>
  /// <returns><see langword="true"/> when it is pure and total.</returns>
  public static bool IsTrapFree(IrExpr expression) => Nodes(expression).All(TrapFreeNode);

  private static bool TrapFreeNode(IrExpr node) => node switch {
    IrConst or IrLocalRef => true,
    IrConstRef reference => !reference.Type.IsArray,
    IrUnary unary => unary.Op != IrUnaryOp.NegateChecked,
    IrBinary binary => !IrOperators.Traps(binary.Op) && !(binary.Left.Type.IsInteger && binary.Op is IrBinaryOp.Divide or IrBinaryOp.Remainder),
    IrConvert conversion => conversion.Contract == IrConversion.Wrap && !conversion.Operand.Type.IsFloat,
    IrConditional => true,
    IrIntrinsic { Op: IrIntrinsicOp.ToInt32 or IrIntrinsicOp.ToUint32 } => true,
    IrIntrinsic intrinsic => intrinsic.Arguments.Count > 0 && intrinsic.Arguments[0].Type.IsInteger && _total.Contains(intrinsic.Op),
    _ => false
  };

  /// <summary>The operations on integers that have a result for every operand.</summary>
  private static readonly HashSet<IrIntrinsicOp> _total = [
    IrIntrinsicOp.RotateLeft, IrIntrinsicOp.RotateRight, IrIntrinsicOp.PopCount, IrIntrinsicOp.LeadingZeroCount, IrIntrinsicOp.TrailingZeroCount,
    IrIntrinsicOp.Parity, IrIntrinsicOp.ByteSwap, IrIntrinsicOp.BitReverse, IrIntrinsicOp.Min, IrIntrinsicOp.Max, IrIntrinsicOp.AbsWrap,
    IrIntrinsicOp.Sign, IrIntrinsicOp.Log2, IrIntrinsicOp.IsolateLowestSet, IrIntrinsicOp.ClearLowestSet, IrIntrinsicOp.MaskToLowestSet
  ];

  /// <summary>Whether an expression reads a variable.</summary>
  /// <param name="expression">The expression.</param>
  /// <param name="local">The variable.</param>
  /// <returns><see langword="true"/> when a node of the expression refers to it.</returns>
  public static bool Reads(IrExpr expression, IrLocal local)
    => Nodes(expression).Any(node => node is IrLocalRef reference && reference.Local == local);

  /// <summary>The variables an expression reads.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>Each variable once.</returns>
  public static IReadOnlyCollection<IrLocal> Locals(IrExpr expression)
    => Nodes(expression).OfType<IrLocalRef>().Select(reference => reference.Local).Distinct().ToList();

  /// <summary>The expression with every read of a variable replaced.</summary>
  /// <param name="expression">The expression.</param>
  /// <param name="local">The variable.</param>
  /// <param name="value">What stands for it; the same object for every read.</param>
  /// <returns>The new expression, sharing the nodes that do not read the variable.</returns>
  public static IrExpr Substitute(IrExpr expression, IrLocal local, IrExpr value) => Map(expression, node
    => node is IrLocalRef reference && reference.Local == local ? value : null);

  /// <summary>The expression with nodes replaced from the bottom up.</summary>
  /// <param name="expression">The expression.</param>
  /// <param name="replace">The replacement of a node, given the node with its operands already mapped, or <see langword="null"/> to keep it.</param>
  /// <returns>The new expression.</returns>
  public static IrExpr Map(IrExpr expression, Func<IrExpr, IrExpr?> replace) {
    var operands = Operands(expression);
    var mapped = operands.Select(operand => Map(operand, replace)).ToList();
    var node = mapped.Where((operand, index) => !ReferenceEquals(operand, operands[index])).Any() ? With(expression, mapped) : expression;
    return replace(node) ?? node;
  }

  /// <summary>An expression with new operands, in the order <see cref="Operands"/> lists them.</summary>
  /// <param name="expression">The expression.</param>
  /// <param name="operands">The new operands.</param>
  /// <returns>The expression rebuilt.</returns>
  public static IrExpr With(IrExpr expression, IReadOnlyList<IrExpr> operands) => expression switch {
    IrUnary unary => new IrUnary(unary.Op, operands[0]),
    IrBinary binary => new IrBinary(binary.Op, operands[0], operands[1]),
    IrConvert conversion => conversion with { Operand = operands[0] },
    IrConditional => new IrConditional(operands[0], operands[1], operands[2]),
    IrArrayLoad => new IrArrayLoad(operands[0], operands[1]),
    IrArrayLength => new IrArrayLength(operands[0]),
    IrNewArray creation => creation with { Length = operands[0] },
    IrCall call => call with { Arguments = operands },
    IrIntrinsic intrinsic => intrinsic with { Arguments = operands },
    IrStep step => step with { Target = operands[0] },
    _ => expression
  };

  /// <summary>The number of nodes of an expression.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The count, the expression included.</returns>
  public static int Size(IrExpr expression) => Nodes(expression).Count();

  private sealed class TreeComparer : IEqualityComparer<IrExpr> {

    public bool Equals(IrExpr? x, IrExpr? y) => x is null ? y is null : y is not null && Same(x, y);

    public int GetHashCode(IrExpr obj) => obj switch {
      IrConst constant => HashCode.Combine(constant.Type, constant.Value),
      IrLocalRef reference => reference.Local.GetHashCode(),
      _ => Operands(obj).Aggregate(HashCode.Combine(obj.GetType(), obj.Type), (hash, operand) => HashCode.Combine(hash, this.GetHashCode(operand)))
    };

  }

}
