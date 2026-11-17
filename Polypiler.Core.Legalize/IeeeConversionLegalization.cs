using System;
using Polypiler.Core.IR;

namespace Polypiler.Core.Legalize;

/// <summary>
/// The conversions of <see cref="IeeeConversions"/> as every target writes them: a node whose contract
/// <see cref="IrConvert"/> states - nearest even or an exact result to floating point, truncation with
/// the overflow trap or with saturation to an integer - is that conversion, a truncating wrap-around of
/// a <c>double</c> to <c>i32</c> or <c>u32</c> is ECMA-262's <see cref="IrIntrinsicOp.ToInt32"/> or
/// <see cref="IrIntrinsicOp.ToUint32"/>, and every other node the call of its helper
/// (<c>IntToDoubleRounded</c>, <c>IntToSingleRounded</c>, <c>DoubleToSingleRounded</c>,
/// <c>FloatToIntBits</c>). <see cref="FloatLegalization.Rewrite"/> applies it for every target.
/// </summary>
public static class IeeeConversionLegalization {

  /// <summary>The rewrite of this legalization.</summary>
  public static LibraryRewrite Rewrite { get; } = Call;

  /// <summary>A node of <see cref="IeeeConversions"/> as a conversion of the IR or the call of its helper; <see langword="null"/> for any other expression.</summary>
  /// <param name="expression">The expression, its operands legalized.</param>
  /// <param name="calls">Makes the calls.</param>
  /// <returns>The replacement, or <see langword="null"/>.</returns>
  public static IrExpr? Call(IrExpr expression, LibraryCalls calls) {
    ArgumentNullException.ThrowIfNull(calls);
    if (expression is not IrIntrinsic intrinsic || !IeeeConversions.Is(intrinsic.Op))
      return null;

    var value = intrinsic.Arguments[0];
    var type = intrinsic.Type;
    var mode = (RoundingMode)((IrConst)intrinsic.Arguments[1]).Value;
    switch (intrinsic.Op) {
      case IrIntrinsicOp.IntToFloat:
        // An integer of up to 16 bits is exact in binary32, one of up to 32 bits in binary64.
        if (mode == RoundingMode.NearestEven || value.Type.Bits <= (type == IrType.F32 ? 16 : 32))
          return calls.Legalize(new IrConvert(type, value));

        return calls.Call(type == IrType.F32 ? "IntToSingleRounded" : "IntToDoubleRounded", type, value, Truth(value.Type == IrType.U64), intrinsic.Arguments[1]);
      case IrIntrinsicOp.FloatConvert:
        if (value.Type == type)
          return value;
        if (type == IrType.F64 || mode == RoundingMode.NearestEven)
          return calls.Legalize(new IrConvert(type, value));

        return calls.Call("DoubleToSingleRounded", type, value, intrinsic.Arguments[1]);
      default:
        return ToInteger(intrinsic, FloatToIntContract.Of(intrinsic), calls);
    }
  }

  private static IrExpr ToInteger(IrIntrinsic intrinsic, FloatToIntContract contract, LibraryCalls calls) {
    var value = intrinsic.Arguments[0];
    var type = intrinsic.Type;
    if (contract is { Rounding: RoundingMode.TowardZero, NanTrap: null }) {
      if (contract is { Policy: RangePolicy.Trap, Trap: IrTrap.IntegerOverflow })
        return calls.Legalize(new IrConvert(type, value, IrConversion.Trap));
      if (contract.Policy == RangePolicy.Saturate)
        return calls.Legalize(new IrConvert(type, value, IrConversion.Saturate));
      if (contract.Policy == RangePolicy.Wrap && (type == IrType.I32 || type == IrType.U32)) {
        var number = value.Type == IrType.F64 ? value : new IrConvert(IrType.F64, value);
        return calls.Legalize(new IrIntrinsic(type == IrType.I32 ? IrIntrinsicOp.ToInt32 : IrIntrinsicOp.ToUint32, type, [number]));
      }
    }

    var minimum = IrOperators.Minimum(type);
    var maximum = IrOperators.Maximum(type);
    return calls.Call("FloatToIntBits", type,
      value, intrinsic.Arguments[1], intrinsic.Arguments[2], Number((double)minimum), Number((double)(maximum + 1)),
      new IrConst(IrType.I64, (long)minimum), new IrConst(IrType.I64, (long)(ulong)maximum), intrinsic.Arguments[3], intrinsic.Arguments[4], intrinsic.Arguments[5]);
  }

  private static IrConst Number(double value) => new(IrType.F64, BitConverter.DoubleToInt64Bits(value));

  private static IrConst Truth(bool value) => new(IrType.Bool, value ? 1 : 0);

}
