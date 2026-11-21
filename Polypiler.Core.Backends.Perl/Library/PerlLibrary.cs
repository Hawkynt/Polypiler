using Polypiler.Core.IR;
using Polypiler.Core.Legalize;

namespace Polypiler.Core.Backends.Perl;

/// <summary>
/// Selects which IR integer and floating-point intrinsics legalization replaces with calls into
/// Polypiler's verified IR library before Perl emission.
/// </summary>
internal static class PerlLibrary {

  /// <summary>Whether Perl lowers an integer intrinsic through <see cref="IntegerLibrary"/>.</summary>
  public static bool Covers(IrIntrinsic intrinsic) {
    if (intrinsic.Arguments.Count == 0 || !IntegerLibrary.Operand(intrinsic).IsInteger)
      return false;

    return intrinsic.Op switch {
      IrIntrinsicOp.Min or IrIntrinsicOp.Max or IrIntrinsicOp.Abs or IrIntrinsicOp.Sign => false,
      IrIntrinsicOp.HighWord or IrIntrinsicOp.JoinWords => false,
      IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig or IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig => false,
      _ => true
    };
  }

  /// <summary>The legalization rewrite for Perl integer intrinsics.</summary>
  public static LibraryRewrite Rewrite()
    => (expression, calls) => expression is IrIntrinsic intrinsic && Covers(intrinsic) ? IntegerLibrary.Call(intrinsic, calls) : null;

}
