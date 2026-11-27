using Polypiler.Core.IR;
using Polypiler.Core.Legalize;

namespace Polypiler.Core.Backends.Kotlin;

/// <summary>
/// The integer operations of the IR a Kotlin profile writes as a helper of the library
/// (<see cref="IntegerLibrary"/>): legalization replaces each by the call of its helper and copies the
/// helper into the module, which then writes it as one of its own functions. What the Kotlin standard
/// library and <c>java.lang</c> have the writer writes itself, at 32 and 64 bits: <c>minOf</c> and
/// <c>maxOf</c> at every width, <c>countOneBits</c>, <c>countLeadingZeroBits</c>,
/// <c>countTrailingZeroBits</c>, <c>rotateLeft</c> and <c>rotateRight</c>, <c>reverseBytes</c>
/// (also of 16 bits) and <c>reverse</c>, the limbs <c>HighWord</c> and <c>JoinWords</c>, and from the
/// JVM releases that have them <c>Math.multiplyHigh</c>, <c>Math.unsignedMultiplyHigh</c>,
/// <c>Integer.compress</c> and <c>Integer.expand</c>.
/// </summary>
internal static class KotlinLibrary {

  /// <summary>Whether the writer writes an integer intrinsic itself on the profile.</summary>
  /// <param name="intrinsic">The node.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="table">The capability table.</param>
  /// <returns>Whether it is written natively.</returns>
  public static bool Native(IrIntrinsic intrinsic, KotlinProfile profile, KotlinCapabilities table) {
    var type = IntegerLibrary.Operand(intrinsic);
    var wide = type.IsWideInteger;
    return intrinsic.Op switch {
      IrIntrinsicOp.Min or IrIntrinsicOp.Max or IrIntrinsicOp.HighWord or IrIntrinsicOp.JoinWords => true,
      IrIntrinsicOp.PopCount or IrIntrinsicOp.LeadingZeroCount or IrIntrinsicOp.TrailingZeroCount or IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight
        or IrIntrinsicOp.BitReverse => wide,
      IrIntrinsicOp.ByteSwap => wide || type.Bits == 16,
      IrIntrinsicOp.MulHigh when type == IrType.I64 => table.Has(KotlinCapabilities.MultiplyHigh, profile),
      IrIntrinsicOp.MulHigh when type == IrType.U64 => table.Has(KotlinCapabilities.UnsignedMultiplyHigh, profile),
      IrIntrinsicOp.ParallelBitExtract or IrIntrinsicOp.ParallelBitDeposit => wide && table.Has(KotlinCapabilities.CompressExpand, profile),
      _ => false
    };
  }

  /// <summary>Whether a profile writes a node with a helper of the library.</summary>
  /// <param name="intrinsic">The node.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="table">The capability table.</param>
  /// <returns>Whether legalization replaces it.</returns>
  public static bool Covers(IrIntrinsic intrinsic, KotlinProfile profile, KotlinCapabilities table)
    => intrinsic.Arguments.Count > 0 && IntegerLibrary.Operand(intrinsic).IsInteger && !Native(intrinsic, profile, table);

  /// <summary>The rewrite of legalization for a profile: every node it covers becomes the call of its helper, where the library has one.</summary>
  /// <param name="profile">The profile.</param>
  /// <param name="table">The capability table.</param>
  /// <returns>The rewrite.</returns>
  public static LibraryRewrite Rewrite(KotlinProfile profile, KotlinCapabilities table)
    => (expression, calls) => expression is IrIntrinsic intrinsic && Covers(intrinsic, profile, table) ? IntegerLibrary.Call(intrinsic, calls) : null;

}
