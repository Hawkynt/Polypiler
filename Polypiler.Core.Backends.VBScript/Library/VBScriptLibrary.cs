using System.Collections.Generic;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;

namespace Polypiler.Core.Backends.VBScript;

/// <summary>
/// Selects which IR integer and floating-point intrinsics legalization replaces with calls into
/// Polypiler's verified IR library before VBScript emission.
/// </summary>
internal static class VBScriptLibrary {

  private static readonly HashSet<IrIntrinsicOp> _nativeFloat = [
    IrIntrinsicOp.Abs, IrIntrinsicOp.Min, IrIntrinsicOp.Max, IrIntrinsicOp.Sign,
    IrIntrinsicOp.Sqrt, IrIntrinsicOp.Floor, IrIntrinsicOp.Ceiling, IrIntrinsicOp.Truncate,
    IrIntrinsicOp.RoundEven, IrIntrinsicOp.RoundAway, IrIntrinsicOp.RoundHalfUp, IrIntrinsicOp.CopySign,
    IrIntrinsicOp.ToInt32, IrIntrinsicOp.ToUint32
  ];

  private static readonly HashSet<IrIntrinsicOp> _nativeInteger = [
    IrIntrinsicOp.Abs, IrIntrinsicOp.Min, IrIntrinsicOp.Max, IrIntrinsicOp.Sign,
    IrIntrinsicOp.RotateLeft, IrIntrinsicOp.RotateRight,
    IrIntrinsicOp.PopCount, IrIntrinsicOp.LeadingZeroCount, IrIntrinsicOp.TrailingZeroCount,
    IrIntrinsicOp.LoadLittle, IrIntrinsicOp.LoadBig, IrIntrinsicOp.StoreLittle, IrIntrinsicOp.StoreBig
  ];

  /// <summary>Whether VBScript emits a floating-point intrinsic directly instead of lowering it.</summary>
  public static bool WritesFloat(IrIntrinsic intrinsic)
    => _nativeFloat.Contains(intrinsic.Op) || !FloatLegalization.IsReplaceable(intrinsic);

  /// <summary>Whether VBScript lowers an integer intrinsic through <see cref="IntegerLibrary"/>.</summary>
  public static bool LowersInteger(IrIntrinsic intrinsic)
    => intrinsic.Arguments.Count > 0
       && IntegerLibrary.Operand(intrinsic).IsInteger
       && !_nativeInteger.Contains(intrinsic.Op);

  /// <summary>The legalization rewrite for VBScript integer intrinsics.</summary>
  public static LibraryRewrite Rewrite()
    => (expression, calls) => expression is IrIntrinsic intrinsic && LowersInteger(intrinsic)
      ? IntegerLibrary.Call(intrinsic, calls)
      : null;

}
