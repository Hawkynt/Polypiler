using Polypiler.Core.IR;
using Polypiler.Core.Legalize;

namespace Polypiler.Core.Backends.Go;

/// <summary>
/// The operations of the IR a Go profile writes as a helper of the library
/// (<see cref="IntegerLibrary"/>): legalization replaces each by the call of its helper and copies the
/// helper into the module, which then writes it as one of its own functions. What Go has, the writer
/// writes itself: the rotates, counts and reversals of <c>math/bits</c> on every width, and the smaller
/// and larger of two integers (the built-ins of Go 1.21, a helper of the backend before).
/// </summary>
internal static class GoLibrary {

  /// <summary>Whether a profile writes a node with a helper of the library.</summary>
  /// <param name="intrinsic">The node.</param>
  /// <returns>Whether legalization replaces it.</returns>
  public static bool Covers(IrIntrinsic intrinsic) {
    if (intrinsic.Arguments.Count == 0 || !IntegerLibrary.Operand(intrinsic).IsInteger)
      return false;

    return intrinsic.Op switch {
      IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight or IrIntrinsicOp.PopCount or IrIntrinsicOp.LeadingZeroCount
        or IrIntrinsicOp.TrailingZeroCount or IrIntrinsicOp.ByteSwap or IrIntrinsicOp.BitReverse or IrIntrinsicOp.Min or IrIntrinsicOp.Max => false,
      // The limbs of O0075 are what the library's helpers are built from, not helpers themselves.
      IrIntrinsicOp.HighWord or IrIntrinsicOp.JoinWords => false,
      // A clamp between constant bounds in order cannot throw: it is min(max(x, low), high).
      IrIntrinsicOp.Clamp => intrinsic.Arguments is not [_, IrConst low, IrConst high]
        || IrOperators.Number(low.Type, low.Value) > IrOperators.Number(high.Type, high.Value),
      _ => true
    };
  }

  /// <summary>The rewrite of legalization: every node it covers becomes the call of its helper, where the library has one.</summary>
  /// <returns>The rewrite.</returns>
  public static LibraryRewrite Rewrite()
    => (expression, calls) => expression is IrIntrinsic intrinsic && Covers(intrinsic) ? IntegerLibrary.Call(intrinsic, calls) : null;

}
