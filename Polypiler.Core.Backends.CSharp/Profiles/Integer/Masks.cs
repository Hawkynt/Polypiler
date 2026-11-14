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
      Helped(IrIntrinsicOp.SignExtend, kind, $"({Type(kind)}){{h}}({Pattern(kind)}, {{1}}, {Digits(Width(kind))})", CSharpIntegerHelpers.SignExtend)
        .WithTotal(new CSharpForm(null, Template: Extended(kind)), arguments => arguments[1] is IrConst bits && bits.Value >= 1 && bits.Value <= Width(kind)),
      Helped(IrIntrinsicOp.ZeroExtend, kind, $"({Type(kind)}){{h}}({Pattern(kind)}, {{1}}, {Digits(Width(kind))})", CSharpIntegerHelpers.ZeroExtend)
    ])
  ];

  /// <summary>
  /// The sign extension of the low bits by a constant count as the shift pair of the signed type the
  /// operators compute in: the constant count stands twice, which is safe for a constant.
  /// </summary>
  private static string Extended(IrTypeKind kind) => kind switch {
    IrTypeKind.I64 => "(({0} << (64 - {1})) >> (64 - {1}))",
    IrTypeKind.U64 => "(ulong)(((long){0} << (64 - {1})) >> (64 - {1}))",
    IrTypeKind.I32 => "(({0} << (32 - {1})) >> (32 - {1}))",
    _ => $"({Type(kind)})(((int){{0}} << (32 - {{1}})) >> (32 - {{1}}))"
  };

}
