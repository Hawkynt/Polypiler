using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// Writes a shift-xor so that RyuJIT does not take it for a rotation (<see cref="CSharpRuntimeDefect.ShiftXorRotation"/>,
/// docs/reference/known-runtime-defects.md). The JIT rotates <c>(v &lt;&lt; y) ^ (v &gt;&gt;&gt; (w - y))</c>
/// - either direction, either order, a count masked to at least the low bits of the width - also where
/// <c>y</c> is 0 modulo the width <c>w</c>, at which C# computes <c>v ^ v = 0</c> and the rotation gives
/// <c>v</c>. It finds the complement <c>w - y</c> (or <c>-y + w</c>) in the expression, and once it optimizes
/// also in a local it substitutes, beside a count it has substituted in turn; it does not find <c>-y</c>,
/// nor a local with its width bit flipped. C# takes a count modulo the width, so <c>w - y</c> and <c>-y</c>
/// are the same count, and so are <c>r</c> and <c>r ^ w</c>: in an exclusive or of a left shift and a
/// logical right shift the first is written for the second and a local the function sets to a complement
/// gets its width bit flipped. Either form costs what the expression costs where the JIT does not rotate,
/// which is the only correct code for it.
/// </summary>
internal static class ShiftXorGuard {

  /// <summary>The exclusive or with every count the JIT could read as a complement written so that it does not.</summary>
  /// <param name="xor">An exclusive or.</param>
  /// <param name="complements">The locals the function sets to a complement, each with the width of the complement.</param>
  /// <returns>The exclusive or itself when it is no shift-xor; otherwise the same with its counts rewritten.</returns>
  public static IrBinary Guarded(IrBinary xor, ISet<(IrLocal Local, int Width)> complements) {
    if (Shift(xor.Left) is not { } left || Shift(xor.Right) is not { } right || IsLeft(left) == IsLeft(right))
      return xor;

    var width = left.Type.Bits;
    return xor with { Left = Rewritten(xor.Left, width, complements), Right = Rewritten(xor.Right, width, complements) };
  }

  /// <summary>Whether a value set to a local is a complement for a width, masked or not: what the JIT reads as one.</summary>
  /// <param name="value">The value.</param>
  /// <param name="width">32 or 64.</param>
  public static bool IsComplement(IrExpr value, int width)
    => Masked(value, width) is { } masked ? IsComplement(Inner(masked), width) : Negated(value, width) is not null;

  /// <summary>
  /// The shift an operand is, through the conversions between the signed and the unsigned type of one
  /// width that the compiled code does not have; <see langword="null"/> for any other operand.
  /// </summary>
  private static IrBinary? Shift(IrExpr operand) => operand switch {
    IrConvert { Contract: IrConversion.Wrap } conversion when conversion.Type.Bits == conversion.Operand.Type.Bits => Shift(conversion.Operand),
    IrBinary { Op: IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRightLogical } shift => shift,
    IrBinary { Op: IrBinaryOp.ShiftRight } shift when !shift.Type.IsSigned => shift,
    _ => null
  };

  private static bool IsLeft(IrBinary shift) => shift.Op == IrBinaryOp.ShiftLeft;

  /// <summary>An operand that <see cref="Shift"/> found a shift in, with the count of that shift rewritten.</summary>
  private static IrExpr Rewritten(IrExpr operand, int width, ISet<(IrLocal, int)> complements) => operand is IrConvert conversion
    ? conversion with { Operand = Rewritten(conversion.Operand, width, complements) }
    : (IrBinary)operand with { Right = Count(((IrBinary)operand).Right, width, complements) };

  /// <summary>A count, a complement in it written <c>-y</c> and a local set to one <c>r ^ w</c>, under every mask that keeps the low bits.</summary>
  private static IrExpr Count(IrExpr count, int width, ISet<(IrLocal, int)> complements) {
    if (Masked(count, width) is { } mask)
      return mask.Right is IrConst ? mask with { Left = Count(mask.Left, width, complements) } : mask with { Right = Count(mask.Right, width, complements) };

    if (Negated(count, width) is { } negated)
      return negated;

    return count is IrLocalRef reference && complements.Contains((reference.Local, width))
      ? new IrBinary(IrBinaryOp.Xor, count, new IrConst(IrType.I32, width))
      : count;
  }

  /// <summary>A count masked with a constant that keeps at least the bits a shift of the width reads, or <see langword="null"/>.</summary>
  private static IrBinary? Masked(IrExpr count, int width)
    => count is IrBinary { Op: IrBinaryOp.And } mask && (mask.Right as IrConst ?? mask.Left as IrConst) is { } constant && (constant.Value & (width - 1)) == width - 1
      ? mask
      : null;

  /// <summary>The operand of a mask that is not its constant.</summary>
  private static IrExpr Inner(IrBinary mask) => mask.Right is IrConst ? mask.Left : mask.Right;

  /// <summary>
  /// <c>-y</c> for a complement <c>w - y</c>, <c>-y + w</c> or <c>w + -y</c> of a count <c>y</c> that is no
  /// constant - with one the C# compiler computes the count itself - or <see langword="null"/>.
  /// </summary>
  private static IrExpr? Negated(IrExpr count, int width) => count switch {
    IrBinary { Op: IrBinaryOp.Subtract, Left: IrConst constant, Right: var y } when constant.Value == width && y is not IrConst => new IrUnary(IrUnaryOp.Negate, y),
    IrBinary { Op: IrBinaryOp.Add, Left: IrUnary { Op: IrUnaryOp.Negate, Operand: not IrConst } negation, Right: IrConst constant } when constant.Value == width => negation,
    IrBinary { Op: IrBinaryOp.Add, Left: IrConst constant, Right: IrUnary { Op: IrUnaryOp.Negate, Operand: not IrConst } negation } when constant.Value == width => negation,
    _ => null
  };

}
