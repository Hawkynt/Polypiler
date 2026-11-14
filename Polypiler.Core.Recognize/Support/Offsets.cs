using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize;

/// <summary>An array index as a base plus a constant offset.</summary>
/// <param name="Base">The part that is not a constant.</param>
/// <param name="Amount">The constant added.</param>
/// <param name="Traps">Whether computing the index can trap: JavaScript's sum in doubles converted back with a trap on overflow.</param>
internal sealed record Offset(IrExpr Base, long Amount, bool Traps) {

  /// <summary>
  /// Splits an index: <c>i + k</c>, <c>k + i</c>, <c>i - k</c>, or what JavaScript's frontend makes of
  /// <c>i + k</c> - the sum of the doubles converted back to an <c>i32</c> with a trap. Anything else
  /// is its own base with offset 0; a constant index is the offset from the base 0.
  /// </summary>
  public static Offset Of(IrExpr index) => index switch {
    IrConst constant => new(Images.Constant(IrType.I32, 0), constant.Value, false),
    IrBinary { Op: IrBinaryOp.Add, Right: IrConst constant } sum => new(sum.Left, constant.Value, false),
    IrBinary { Op: IrBinaryOp.Add, Left: IrConst constant } sum => new(sum.Right, constant.Value, false),
    IrBinary { Op: IrBinaryOp.Subtract, Right: IrConst constant } difference => new(difference.Left, -constant.Value, false),
    IrConvert { Contract: IrConversion.Trap, Type.Kind: IrTypeKind.I32, Operand: IrBinary { Op: IrBinaryOp.Add, Left: IrConvert { Operand.Type.Kind: IrTypeKind.I32 } widened, Right: IrConst { Type.Kind: IrTypeKind.F64 } constant } }
      when IrOperators.Float(IrType.F64, constant.Value) is var number && number == System.Math.Floor(number) && System.Math.Abs(number) <= 64
      => new(widened.Operand, (long)number, true),
    _ => new(index, 0, false)
  };

  /// <summary>The index of the base plus a constant, as wrapping <c>i32</c> arithmetic computes it.</summary>
  public static IrExpr At(IrExpr @base, long offset) => (@base, offset) switch {
    (_, 0) => @base,
    (IrConst constant, _) => Images.Constant(IrType.I32, constant.Value + offset),
    _ => new IrBinary(IrBinaryOp.Add, @base, Images.Constant(IrType.I32, offset))
  };

}
