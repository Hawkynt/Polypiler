using Polypiler.Core.IR;
using Polypiler.Core.Legalize;

namespace Polypiler.Core.Backends.Python;

/// <summary>
/// The operations of the IR a Python profile writes as a helper of the library
/// (<see cref="IntegerLibrary"/>): legalization replaces each by the call of its helper and copies the
/// helper into the module, which then writes it as one of its own functions. Python has none of the
/// rotates, counts, reorderings, powers of two, roundings of a quotient, clamps, bit fields, masks,
/// saturating or overflow-testing arithmetic and endian accesses; what it has - <c>min</c> and
/// <c>max</c> of integers of every width, and <c>int.bit_count</c> from 3.10 - the writer writes itself.
/// </summary>
internal static class PythonLibrary {

  /// <summary>Whether a profile writes a node with a helper of the library.</summary>
  /// <param name="intrinsic">The node.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="table">The capability table.</param>
  /// <returns>Whether legalization replaces it.</returns>
  public static bool Covers(IrIntrinsic intrinsic, PythonProfile profile, PythonCapabilities table) {
    if (intrinsic.Arguments.Count == 0 || !IntegerLibrary.Operand(intrinsic).IsInteger)
      return false;

    return intrinsic.Op switch {
      IrIntrinsicOp.Min or IrIntrinsicOp.Max => false,
      IrIntrinsicOp.PopCount => !table.Has(PythonCapabilities.BitCount, profile),
      _ => true
    };
  }

  /// <summary>The rewrite of legalization for a profile: every node it covers becomes the call of its helper, where the library has one.</summary>
  /// <param name="profile">The profile.</param>
  /// <param name="table">The capability table.</param>
  /// <returns>The rewrite.</returns>
  public static LibraryRewrite Rewrite(PythonProfile profile, PythonCapabilities table)
    => (expression, calls) => expression is IrIntrinsic intrinsic && Covers(intrinsic, profile, table) ? IntegerLibrary.Call(intrinsic, calls) : null;

}
