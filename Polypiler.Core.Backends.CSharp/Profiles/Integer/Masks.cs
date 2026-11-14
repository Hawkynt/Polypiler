using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// Masks and the extensions of a field at the bottom: a helper in every profile on the 64 bits of
  /// the pattern, the result cut back to the type. A mask is looked up by the type of its result,
  /// since its one argument is the count.
  /// </summary>
  private static IEnumerable<CSharpCapability> Masks => [
    .. _all.SelectMany(kind => (IEnumerable<CSharpCapability>)[
      Helped(IrIntrinsicOp.LowMask, kind, $"({Type(kind)}){{h}}({{0}}, {Digits(Width(kind))})", CSharpIntegerHelpers.LowMask),
      Helped(IrIntrinsicOp.IsolateLowestSet, kind, $"({Type(kind)}){{h}}({Pattern(kind)})", CSharpIntegerHelpers.IsolateLowestSet),
      Helped(IrIntrinsicOp.ClearLowestSet, kind, $"({Type(kind)}){{h}}({Pattern(kind)})", CSharpIntegerHelpers.ClearLowestSet),
      Helped(IrIntrinsicOp.MaskToLowestSet, kind, $"({Type(kind)}){{h}}({Pattern(kind)})", CSharpIntegerHelpers.MaskToLowestSet),
      Helped(IrIntrinsicOp.SignExtend, kind, $"({Type(kind)}){{h}}({Pattern(kind)}, {{1}}, {Digits(Width(kind))})", CSharpIntegerHelpers.SignExtend),
      Helped(IrIntrinsicOp.ZeroExtend, kind, $"({Type(kind)}){{h}}({Pattern(kind)}, {{1}}, {Digits(Width(kind))})", CSharpIntegerHelpers.ZeroExtend)
    ])
  ];

}
