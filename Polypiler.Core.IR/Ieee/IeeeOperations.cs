using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The IEEE 754 operations of the IR that carry their rounding mode or their predicate on the node
/// (#116): <see cref="IrIntrinsicOp.FloatAdd"/>, <see cref="IrIntrinsicOp.FloatSubtract"/>,
/// <see cref="IrIntrinsicOp.FloatMultiply"/> and <see cref="IrIntrinsicOp.FloatDivide"/> with a
/// <see cref="RoundingMode"/>, <see cref="IrIntrinsicOp.FloatCompare"/> with one of the fourteen
/// <see cref="FloatPredicate"/>s and <see cref="IrIntrinsicOp.FloatClassify"/>. The mode and the
/// predicate are the last argument, an <c>i32</c> constant. The interpreter computes them exactly in
/// the format of the operands (<see cref="IeeeFormat"/>); legalization makes the ones with
/// <c>rne</c> and the plain comparisons operators of the IR and the rest calls of their helpers.
/// </summary>
public static class IeeeOperations {

  private static readonly HashSet<IrIntrinsicOp> _arithmetic = [IrIntrinsicOp.FloatAdd, IrIntrinsicOp.FloatSubtract, IrIntrinsicOp.FloatMultiply, IrIntrinsicOp.FloatDivide];

  private static readonly HashSet<IrIntrinsicOp> _all = [.. _arithmetic, IrIntrinsicOp.FloatCompare, IrIntrinsicOp.FloatClassify];

  /// <summary>The operations of this class.</summary>
  public static IReadOnlyCollection<IrIntrinsicOp> Operations => _all;

  /// <summary>Whether an operation is one of this class.</summary>
  /// <param name="op">The operation.</param>
  public static bool Is(IrIntrinsicOp op) => _all.Contains(op);

  /// <summary>Whether an operation is one of the four that round.</summary>
  /// <param name="op">The operation.</param>
  public static bool IsArithmetic(IrIntrinsicOp op) => _arithmetic.Contains(op);

  /// <summary>An arithmetic operation rounded in a mode.</summary>
  /// <param name="op"><see cref="IrIntrinsicOp.FloatAdd"/>, <see cref="IrIntrinsicOp.FloatSubtract"/>, <see cref="IrIntrinsicOp.FloatMultiply"/> or <see cref="IrIntrinsicOp.FloatDivide"/>.</param>
  /// <param name="left">The first operand.</param>
  /// <param name="right">The second operand.</param>
  /// <param name="mode">The rounding mode.</param>
  /// <returns>The node, of the type of the first operand.</returns>
  /// <exception cref="ArgumentException">The operation is none of the four.</exception>
  public static IrIntrinsic Arithmetic(IrIntrinsicOp op, IrExpr left, IrExpr right, RoundingMode mode) {
    ArgumentNullException.ThrowIfNull(left);
    ArgumentNullException.ThrowIfNull(right);
    if (!IsArithmetic(op))
      throw new ArgumentException($"{op} is no rounded arithmetic operation.", nameof(op));

    return new(op, left.Type, [left, right, Constant((int)mode)]);
  }

  /// <summary>A comparison of two numbers.</summary>
  /// <param name="predicate">The comparison.</param>
  /// <param name="left">The first operand.</param>
  /// <param name="right">The second operand.</param>
  /// <returns>The node, a truth value.</returns>
  public static IrIntrinsic Compare(FloatPredicate predicate, IrExpr left, IrExpr right) {
    ArgumentNullException.ThrowIfNull(left);
    ArgumentNullException.ThrowIfNull(right);
    return new(IrIntrinsicOp.FloatCompare, IrType.Bool, [left, right, Constant((int)predicate)]);
  }

  /// <summary>The class of a number.</summary>
  /// <param name="value">The number.</param>
  /// <returns>The node, a <c>u8</c> of the bits of <see cref="FloatClass"/>.</returns>
  public static IrIntrinsic Classify(IrExpr value) {
    ArgumentNullException.ThrowIfNull(value);
    return new(IrIntrinsicOp.FloatClassify, IrType.U8, [value]);
  }

  /// <summary>The rounding mode of a node of a rounded arithmetic operation whose mode is a constant.</summary>
  /// <param name="intrinsic">The node.</param>
  /// <returns>The mode.</returns>
  public static RoundingMode ModeOf(IrIntrinsic intrinsic) => (RoundingMode)((IrConst)intrinsic.Arguments[2]).Value;

  /// <summary>The predicate of a node of <see cref="IrIntrinsicOp.FloatCompare"/> whose predicate is a constant.</summary>
  /// <param name="intrinsic">The node.</param>
  /// <returns>The predicate.</returns>
  public static FloatPredicate PredicateOf(IrIntrinsic intrinsic) => (FloatPredicate)((IrConst)intrinsic.Arguments[2]).Value;

  /// <summary>Whether an operation of this class takes the types, or <see langword="null"/> for an operation of another.</summary>
  internal static bool? Accepts(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> arguments) {
    if (!Is(op))
      return null;

    if (arguments.Count == 0 || IeeeFormats.Default.Of(arguments[0]) is null)
      return false;

    var format = arguments[0];
    return op switch {
      IrIntrinsicOp.FloatClassify => result == IrType.U8 && arguments.Count == 1,
      IrIntrinsicOp.FloatCompare => result == IrType.Bool && arguments.SequenceEqual([format, format, IrType.I32]),
      _ => result == format && arguments.SequenceEqual([format, format, IrType.I32])
    };
  }

  /// <summary>
  /// What is wrong with a node of this class beyond its types: the mode or the predicate has to be a
  /// constant that names one.
  /// </summary>
  /// <param name="intrinsic">The node.</param>
  /// <returns>The problem, or <see langword="null"/>.</returns>
  public static string? Problem(IrIntrinsic intrinsic) {
    ArgumentNullException.ThrowIfNull(intrinsic);
    if (!Is(intrinsic.Op) || intrinsic.Op == IrIntrinsicOp.FloatClassify || intrinsic.Arguments.Count != 3)
      return null;

    var (what, count) = intrinsic.Op == IrIntrinsicOp.FloatCompare ? ("predicate", 14) : ("rounding mode", 5);
    return intrinsic.Arguments[2] switch {
      IrConst { Value: var value } when value >= 0 && value < count => null,
      IrConst { Value: var value } => $"unknown {what} {value} of {intrinsic.Op}",
      _ => $"the {what} of {intrinsic.Op} is no constant"
    };
  }

  /// <summary>Whether the interpreter is to call this class for a node.</summary>
  internal static bool Evaluates(IrIntrinsic intrinsic) => Is(intrinsic.Op);

  /// <summary>Evaluates a node of a well-typed module on the values of its arguments, as the interpreter carries them.</summary>
  /// <param name="intrinsic">The node.</param>
  /// <param name="arguments">A <see cref="float"/> or <see cref="double"/> per number, a <see cref="long"/> for the mode or predicate.</param>
  /// <returns>The result as the interpreter carries it.</returns>
  public static object Evaluate(IrIntrinsic intrinsic, object[] arguments) {
    ArgumentNullException.ThrowIfNull(intrinsic);
    ArgumentNullException.ThrowIfNull(arguments);
    var type = intrinsic.Arguments[0].Type;
    var format = IeeeFormats.Default.Of(type)!;
    var left = IeeeFormats.BitsOf(arguments[0]);
    if (intrinsic.Op == IrIntrinsicOp.FloatClassify)
      return (long)format.Classify(left);

    var right = IeeeFormats.BitsOf(arguments[1]);
    var selector = (int)(long)arguments[2];
    if (intrinsic.Op == IrIntrinsicOp.FloatCompare)
      return format.Compare(left, right, (FloatPredicate)selector);

    var mode = (RoundingMode)selector;
    var bits = intrinsic.Op switch {
      IrIntrinsicOp.FloatAdd => format.Add(left, right, mode),
      IrIntrinsicOp.FloatSubtract => format.Subtract(left, right, mode),
      IrIntrinsicOp.FloatMultiply => format.Multiply(left, right, mode),
      _ => format.Divide(left, right, mode)
    };
    return IeeeFormats.ValueOf(type, bits);
  }

  private static IrConst Constant(int value) => new(IrType.I32, value);

}
