using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// The bit permutations under a mask, the byte shuffle and the CRC update: a helper in every
  /// profile. <c>Bmi2.ParallelBitExtract</c> and <c>Bmi2.ParallelBitDeposit</c> exist from .NET Core 3.0
  /// on, but only on x86 with BMI2, and throw <c>PlatformNotSupportedException</c> elsewhere; the
  /// helper computes the same on every processor. <c>Ssse3.Shuffle</c> works on a <c>Vector128</c>,
  /// not on the 64 bits of the IR's widest integer, and no framework has a CRC of every polynomial.
  /// </summary>
  private static IEnumerable<CSharpCapability> Permutations => [
    .. _all.SelectMany(kind => (IEnumerable<CSharpCapability>)[
      Helped(IrIntrinsicOp.ParallelBitExtract, kind),
      Helped(IrIntrinsicOp.ParallelBitDeposit, kind)
    ]),
    Helped(IrIntrinsicOp.ByteShuffle, IrTypeKind.U64),
    .. _unsigned.Select(kind => Helped(IrIntrinsicOp.CrcUpdate, kind))
  ];

}
