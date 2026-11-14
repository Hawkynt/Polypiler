using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// The byte swap is <c>BinaryPrimitives.ReverseEndianness</c> from .NET Standard 2.1 on, which has
  /// an overload for every width; the bit reversal is a helper everywhere, as no framework has one.
  /// </summary>
  private static IEnumerable<CSharpCapability> Reordering => [
    .. _all.Select(kind => Row(IrIntrinsicOp.ByteSwap, kind, CSharpProfile.NetStandard21, Binary + "ReverseEndianness", $"{Binary}ReverseEndianness({{0}})",
      $"({Type(kind)}){{h}}({Pattern(kind)}, {Digits(Width(kind))})", CSharpIntegerHelpers.ByteSwapBits)),
    .. _all.Select(kind => Helped(IrIntrinsicOp.BitReverse, kind, $"({Type(kind)}){{h}}({Pattern(kind)}, {Digits(Width(kind))})", CSharpIntegerHelpers.BitReverseBits))
  ];

}
