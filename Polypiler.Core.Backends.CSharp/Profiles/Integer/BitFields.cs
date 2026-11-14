using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// Bit fields and single bits: a helper in every profile, which no framework has, on the 64 bits of
  /// the pattern and told the width, so that it traps on a field or a position outside it.
  /// </summary>
  private static IEnumerable<CSharpCapability> BitFields => [
    .. _all.SelectMany(kind => (IEnumerable<CSharpCapability>)[
      Helped(IrIntrinsicOp.ExtractBits, kind, $"({Type(kind)}){{h}}({Pattern(kind)}, {{1}}, {{2}}, {Digits(Width(kind))})", CSharpIntegerHelpers.ExtractBits),
      Helped(IrIntrinsicOp.InsertBits, kind, $"({Type(kind)}){{h}}({Pattern(kind)}, {Pattern(kind, "{1}")}, {{2}}, {{3}}, {Digits(Width(kind))})", CSharpIntegerHelpers.InsertBits),
      Helped(IrIntrinsicOp.TestBit, kind, $"{{h}}({Pattern(kind)}, {{1}}, {Digits(Width(kind))})", CSharpIntegerHelpers.TestBit),
      Helped(IrIntrinsicOp.SetBit, kind, $"({Type(kind)}){{h}}({Pattern(kind)}, {{1}}, {Digits(Width(kind))})", CSharpIntegerHelpers.SetBit),
      Helped(IrIntrinsicOp.ClearBit, kind, $"({Type(kind)}){{h}}({Pattern(kind)}, {{1}}, {Digits(Width(kind))})", CSharpIntegerHelpers.ClearBit),
      Helped(IrIntrinsicOp.ToggleBit, kind, $"({Type(kind)}){{h}}({Pattern(kind)}, {{1}}, {Digits(Width(kind))})", CSharpIntegerHelpers.ToggleBit)
    ])
  ];

}
