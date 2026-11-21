using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// The byte swap is <c>BinaryPrimitives.ReverseEndianness</c> from .NET Standard 2.1 on, which has
  /// an overload for every width; the bit reversal and the reversal of the bits in each byte are a
  /// helper everywhere, as no framework has them.
  /// </summary>
  private static IEnumerable<CSharpCapability> Reordering => [
    .. _all.Select(kind => Library(IrIntrinsicOp.ByteSwap, kind, CSharpProfile.NetStandard21, Binary + "ReverseEndianness", $"{Binary}ReverseEndianness({{0}})")),
    .. _all.Select(kind => Helped(IrIntrinsicOp.BitReverse, kind)),
    .. _all.Select(kind => Helped(IrIntrinsicOp.ReverseBitsInBytes, kind))
  ];

}
