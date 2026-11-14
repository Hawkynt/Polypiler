using System;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize;

/// <summary>The questions the rules ask of a <see cref="WeightedValue"/>, and the small builders their replacements share.</summary>
internal static class Images {

  /// <summary>
  /// The atom a value is a permutation of and the position each atom bit lands on, when the value is
  /// exactly the low <c>k</c> bits of one atom - every one once, with nothing else - on its low
  /// <c>k</c> bits, <c>k</c> being 8, 16, 32 or 64. When <c>k</c> is the width of the value the atom
  /// keeps its type; otherwise it is cut to the unsigned type of <c>k</c> bits, which the value
  /// extends with zeros.
  /// </summary>
  public static (IrExpr Atom, int[] Positions)? Permutation(WeightedValue value) {
    var image = value.Image;
    var width = image.Terms.Count;
    if (value.Single is not { } atom || image.Constant != 0 || width is not (8 or 16 or 32 or 64) || width > image.Width || atom.Type.Bits < width
      || image.Terms.Keys.Any(key => (key & 63) >= width))
      return null;

    var positions = new int[width];
    var seen = 0UL;
    foreach (var (key, weight) in image.Terms) {
      if ((weight & (weight - 1)) != 0)
        return null;

      positions[key & 63] = System.Numerics.BitOperations.TrailingZeroCount(weight);
      seen |= weight;
    }

    if (seen != Weights.MaskOf(width))
      return null;

    if (atom.Type.Bits > width || (width < image.Width && atom.Type.IsSigned))
      atom = new IrConvert(Unsigned(width), atom);

    return (atom, positions);
  }

  /// <summary>The amount a value rotates its atom to the left by, when it is a rotation.</summary>
  public static (IrExpr Atom, int Count)? Rotation(WeightedValue value) {
    if (Permutation(value) is not var (atom, positions))
      return null;

    var width = positions.Length;
    var count = positions[0];
    return positions.Select((position, bit) => position == (bit + count) % width).All(holds => holds) ? (atom, count) : null;
  }

  /// <summary>Whether a value is its atom with a fixed permutation of the bits.</summary>
  public static IrExpr? Permuted(WeightedValue value, Func<int, int, int> position) {
    if (Permutation(value) is not var (atom, positions))
      return null;

    var width = positions.Length;
    return positions.Select((landing, bit) => landing == position(bit, width)).All(holds => holds) ? atom : null;
  }

  /// <summary>The unsigned integer type of a width.</summary>
  public static IrType Unsigned(int width) => width switch {
    8 => IrType.U8,
    16 => IrType.U16,
    32 => IrType.U32,
    _ => IrType.U64
  };

  /// <summary>An expression in a type: itself, or a conversion that wraps around.</summary>
  public static IrExpr Fit(IrExpr expression, IrType type) => expression.Type == type ? expression : new IrConvert(type, expression);

  /// <summary>An integer constant of a type.</summary>
  public static IrConst Constant(IrType type, long value) => new(type, IrOperators.Wrap(type, value));

  /// <summary>An operation of the IR on arguments.</summary>
  public static IrIntrinsic Call(IrIntrinsicOp op, IrType type, params IrExpr[] arguments) => new(op, type, arguments);

  /// <summary>Whether an expression is a node a recognition rule replaces: an operator or a conversion, never a leaf or an intrinsic.</summary>
  public static bool IsCandidate(IrExpr expression) => expression is IrBinary or IrConvert or IrUnary && expression.Type.IsInteger;

  /// <summary>An expression without the conversions between integer types of one width around it, which change no bit.</summary>
  public static IrExpr Peel(IrExpr expression) {
    while (expression is IrConvert { Contract: IrConversion.Wrap } conversion && conversion.Operand.Type.IsInteger && conversion.Type.IsInteger
      && conversion.Operand.Type.Bits == conversion.Type.Bits)
      expression = conversion.Operand;

    return expression;
  }

  /// <summary>An expression without the widening conversions around it, which change no value.</summary>
  public static IrExpr Unwiden(IrExpr expression) {
    while (expression is IrConvert { Contract: IrConversion.Wrap } conversion && conversion.Operand.Type.IsInteger && conversion.Type.IsInteger
      && IrOperators.Contains(conversion.Type, conversion.Operand.Type))
      expression = conversion.Operand;

    return expression;
  }

  /// <summary>Whether an expression is an integer constant, and which.</summary>
  public static bool IsConstant(IrExpr expression, out long value) {
    value = expression is IrConst constant && constant.Type.IsInteger ? constant.Value : 0;
    return expression is IrConst { Type.IsInteger: true };
  }

  /// <summary>
  /// Whether an expression computes, bit for bit, a given function of one atom: its weighted bits
  /// are those of <paramref name="form"/> applied to the atom's bits, and its one atom is the given
  /// expression (or it has none).
  /// </summary>
  public static bool Computes(IrExpr expression, IrExpr atom, Func<Weights, Weights> form, RuleContext context)
    => WeightEvaluator.Evaluate(expression, context) is { } value && value.Image.Width == atom.Type.Bits
      && value.Atoms.Count <= 1 && (value.Atoms.Count == 0 || IrShape.Same(value.Atoms[0], atom))
      && value.Image.SameAs(form(Weights.Atom(0, atom.Type.Bits)));

  /// <summary>
  /// Whether two expressions are the same tree, or two integer constants of the same number - a
  /// constant that was compared after a widening and the same constant of the narrow type.
  /// </summary>
  public static bool SameValue(IrExpr left, IrExpr right)
    => IrShape.Same(left, right) || left is IrConst { Type.IsInteger: true } one && right is IrConst { Type.IsInteger: true } other
      && IrOperators.Number(one.Type, one.Value) == IrOperators.Number(other.Type, other.Value);

  /// <summary>The negation of an expression: <c>-x</c> or <c>0 - x</c>; <see langword="null"/> otherwise.</summary>
  public static IrExpr? Negated(IrExpr expression) => expression switch {
    IrUnary { Op: IrUnaryOp.Negate } unary => unary.Operand,
    IrBinary { Op: IrBinaryOp.Subtract, Left: IrConst { Value: 0 } } binary => binary.Right,
    _ => null
  };

  /// <summary>The comparison with its operands swapped: <c>a &lt; b</c> as <c>b &gt; a</c>.</summary>
  public static IrBinaryOp Mirror(IrBinaryOp op) => op switch {
    IrBinaryOp.Less => IrBinaryOp.Greater,
    IrBinaryOp.LessOrEqual => IrBinaryOp.GreaterOrEqual,
    IrBinaryOp.Greater => IrBinaryOp.Less,
    IrBinaryOp.GreaterOrEqual => IrBinaryOp.LessOrEqual,
    _ => op
  };

  /// <summary>The value of a stable local that is its definition, or the expression itself.</summary>
  public static IrExpr Through(IrExpr expression, RuleContext context)
    => expression is IrLocalRef reference && context.Definition(reference.Local) is { } definition ? definition : expression;

}
