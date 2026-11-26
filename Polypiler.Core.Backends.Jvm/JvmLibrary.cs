using Polypiler.Core.IR;
using Polypiler.Core.Legalize;

namespace Polypiler.Core.Backends.Jvm;

/// <summary>
/// Selects which IR floating-point and integer intrinsics are lowered to Polypiler's verified IR
/// library functions during <see cref="JvmEmitter.Legalize"/> versus emitted directly as JVM bytecode.
/// </summary>
internal static class JvmLibrary {

  /// <summary>Whether the JVM emitter lowers a floating-point intrinsic natively without library expansion.</summary>
  public static bool WritesFloat(IrIntrinsic intrinsic) => intrinsic.Op switch {
    IrIntrinsicOp.Abs or IrIntrinsicOp.Min or IrIntrinsicOp.Max or IrIntrinsicOp.Sign
      or IrIntrinsicOp.Sqrt or IrIntrinsicOp.Floor or IrIntrinsicOp.Ceiling or IrIntrinsicOp.Truncate
      or IrIntrinsicOp.RoundEven or IrIntrinsicOp.RoundAway or IrIntrinsicOp.CopySign
      or IrIntrinsicOp.FloatBits or IrIntrinsicOp.BitsFloat
      or IrIntrinsicOp.IsNaN or IrIntrinsicOp.IsInfinity or IrIntrinsicOp.IsFinite
      or IrIntrinsicOp.IsNormal or IrIntrinsicOp.IsSubnormal or IrIntrinsicOp.IsNegative => true,
    _ => !FloatLegalization.IsReplaceable(intrinsic)
  };

  /// <summary>Whether an integer intrinsic is rewritten via <see cref="IntegerLibrary"/> before JVM emission.</summary>
  public static bool LowersInteger(IrIntrinsic intrinsic) {
    if (intrinsic.Arguments.Count == 0 || !IntegerLibrary.Operand(intrinsic).IsInteger)
      return false;

    return intrinsic.Op switch {
      IrIntrinsicOp.Abs or IrIntrinsicOp.Min or IrIntrinsicOp.Max
        or IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight
        or IrIntrinsicOp.LeadingZeroCount or IrIntrinsicOp.TrailingZeroCount or IrIntrinsicOp.PopCount
        or IrIntrinsicOp.ByteSwap or IrIntrinsicOp.HighWord or IrIntrinsicOp.JoinWords
        or IrIntrinsicOp.ParallelBitExtract or IrIntrinsicOp.ParallelBitDeposit
        or IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig
        or IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig => false,
      _ => true
    };
  }

  /// <summary>The legalization rewrite for JVM integer intrinsics.</summary>
  public static LibraryRewrite IntegerRewrite()
    => (expression, calls) => expression is IrIntrinsic intrinsic && LowersInteger(intrinsic) ? IntegerLibrary.Call(intrinsic, calls) : null;

  /// <summary>The legalization rewrite for ECMA-262 numeric intrinsics (<c>ToInt32</c>, <c>ToUint32</c>, <c>RoundHalfUp</c>).</summary>
  public static LibraryRewrite EcmaRewrite()
    => (expression, calls) => expression is IrIntrinsic { Op: IrIntrinsicOp.ToInt32 or IrIntrinsicOp.ToUint32 or IrIntrinsicOp.RoundHalfUp } intrinsic
      ? calls.Call(intrinsic.Op.ToString(), intrinsic.Type, intrinsic.Arguments)
      : null;

}
