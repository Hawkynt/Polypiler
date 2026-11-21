using System.Globalization;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>
  /// What is wrong with a node whose types the class accepts, where a constant argument already
  /// decides that it would trap on every run: a truth table of <see cref="IrIntrinsicOp.TernaryLogic"/>
  /// outside 0 to 255, a bit position of a single-bit operation outside the width, a start or a length
  /// of a field outside 0 to the width, or a constant start and length whose field reaches past it.
  /// The verifier refuses such a node; a variable argument is checked when it is evaluated. This is
  /// the strict contract of a position; the masked one is the position <c>And(n, width - 1)</c>, which
  /// is always inside a width that is a power of two.
  /// </summary>
  /// <param name="intrinsic">The node.</param>
  /// <returns>The problem, or <see langword="null"/> where there is none.</returns>
  public static string? Problem(IrIntrinsic intrinsic) {
    System.ArgumentNullException.ThrowIfNull(intrinsic);
    if (!Evaluates(intrinsic))
      return null;

    var width = Width(intrinsic.Arguments[0].Type);
    return intrinsic.Op switch {
      IrIntrinsicOp.TernaryLogic when intrinsic.Arguments[3] is IrConst { Value: < 0 or > 255 } table
        => $"intrinsic 'TernaryLogic': the truth table {Text(table.Value)} is outside [0, 255]",
      IrIntrinsicOp.TestBit or IrIntrinsicOp.SetBit or IrIntrinsicOp.ClearBit or IrIntrinsicOp.ToggleBit
        when intrinsic.Arguments[1] is IrConst position && (position.Value < 0 || position.Value >= width)
        => $"intrinsic '{intrinsic.Op}': the position {Text(position.Value)} is outside [0, {Text(width - 1)}]",
      IrIntrinsicOp.ExtractBits or IrIntrinsicOp.ExtractBitsSigned => FieldProblem(intrinsic, intrinsic.Arguments[1], intrinsic.Arguments[2], width),
      IrIntrinsicOp.InsertBits => FieldProblem(intrinsic, intrinsic.Arguments[2], intrinsic.Arguments[3], width),
      _ => null
    };
  }

  /// <summary>The problem of a field whose start or length is a constant outside the width, or both are constants whose field reaches past it.</summary>
  private static string? FieldProblem(IrIntrinsic intrinsic, IrExpr start, IrExpr length, int width) {
    if (start is IrConst { Value: var first } && (first < 0 || first > width))
      return $"intrinsic '{intrinsic.Op}': the start {Text(first)} is outside [0, {Text(width)}]";
    if (length is IrConst { Value: var count } && (count < 0 || count > width))
      return $"intrinsic '{intrinsic.Op}': the length {Text(count)} is outside [0, {Text(width)}]";

    return start is IrConst { Value: var from } && length is IrConst { Value: var bits } && from + bits > width
      ? $"intrinsic '{intrinsic.Op}': the field of {Text(bits)} bits from bit {Text(from)} reaches past bit {Text(width - 1)}"
      : null;
  }

  private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

}
