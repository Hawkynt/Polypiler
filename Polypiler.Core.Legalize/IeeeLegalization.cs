using System;
using Polypiler.Core.IR;

namespace Polypiler.Core.Legalize;

/// <summary>
/// The IEEE 754 operations of <see cref="IeeeOperations"/> as every target writes them: an arithmetic
/// operation that rounds to nearest even is the operator of the IR, which every target has; one in
/// another mode is the call of its helper (<c>AddRounded64</c>, …), which computes it on integers. A
/// comparison that an operator of the IR states - <c>oeq</c>, <c>une</c>, <c>olt</c>, <c>ole</c>,
/// <c>ogt</c>, <c>oge</c> - is that operator; the other eight are the call of <c>CompareFloat</c>. The
/// classification is the call of <c>ClassifyFloat32</c> or <c>ClassifyFloat64</c>. No backend writes
/// these operations itself, so <see cref="FloatLegalization.Rewrite"/> applies this rewrite for every
/// target.
/// </summary>
public static class IeeeLegalization {

  /// <summary>The rewrite of this legalization.</summary>
  public static LibraryRewrite Rewrite { get; } = Call;

  /// <summary>A node of <see cref="IeeeOperations"/> as operators of the IR or the call of its helper; <see langword="null"/> for any other expression.</summary>
  /// <param name="expression">The expression, its operands legalized.</param>
  /// <param name="calls">Makes the calls.</param>
  /// <returns>The replacement, or <see langword="null"/>.</returns>
  public static IrExpr? Call(IrExpr expression, LibraryCalls calls) {
    ArgumentNullException.ThrowIfNull(calls);
    if (expression is not IrIntrinsic intrinsic || !IeeeOperations.Is(intrinsic.Op))
      return null;

    var arguments = intrinsic.Arguments;
    var single = arguments[0].Type == IrType.F32;
    if (intrinsic.Op == IrIntrinsicOp.FloatClassify)
      return calls.Call(single ? "ClassifyFloat32" : "ClassifyFloat64", intrinsic.Type, arguments[0]);

    if (intrinsic.Op == IrIntrinsicOp.FloatCompare) {
      IrBinaryOp? op = IeeeOperations.PredicateOf(intrinsic) switch {
        FloatPredicate.OrderedEqual => IrBinaryOp.Equal,
        FloatPredicate.UnorderedNotEqual => IrBinaryOp.NotEqual,
        FloatPredicate.OrderedLess => IrBinaryOp.Less,
        FloatPredicate.OrderedLessOrEqual => IrBinaryOp.LessOrEqual,
        FloatPredicate.OrderedGreater => IrBinaryOp.Greater,
        FloatPredicate.OrderedGreaterOrEqual => IrBinaryOp.GreaterOrEqual,
        _ => null
      };
      return op is { } binary
        ? calls.Legalize(new IrBinary(binary, arguments[0], arguments[1]))
        : calls.Call("CompareFloat", IrType.Bool, arguments[0], arguments[1], arguments[2]);
    }

    if (IeeeOperations.ModeOf(intrinsic) == RoundingMode.NearestEven)
      return calls.Legalize(new IrBinary(intrinsic.Op switch {
        IrIntrinsicOp.FloatAdd => IrBinaryOp.Add,
        IrIntrinsicOp.FloatSubtract => IrBinaryOp.Subtract,
        IrIntrinsicOp.FloatMultiply => IrBinaryOp.Multiply,
        _ => IrBinaryOp.Divide
      }, arguments[0], arguments[1]));

    var name = intrinsic.Op switch {
      IrIntrinsicOp.FloatAdd => "AddRounded",
      IrIntrinsicOp.FloatSubtract => "SubtractRounded",
      IrIntrinsicOp.FloatMultiply => "MultiplyRounded",
      _ => "DivideRounded"
    };
    return calls.Call(name + (single ? "32" : "64"), intrinsic.Type, arguments);
  }

}
