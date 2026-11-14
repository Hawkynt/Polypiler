using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// Saturating arithmetic, which no framework has as a method of the base library before the generic
  /// math of .NET 7. Up to 32 bits the exact result is computed in 64 bits and clamped to the range
  /// of the type; of 64 bits a helper per operation tells overflow apart.
  /// </summary>
  private static IEnumerable<CSharpCapability> Saturating => [
    .. _all.SelectMany(kind => Width(kind) == 64
      ? (IEnumerable<CSharpCapability>)[
        Helped(IrIntrinsicOp.AddSaturate, kind, "{h}({0}, {1})", IsSigned(kind) ? CSharpIntegerHelpers.AddSaturateSigned64 : CSharpIntegerHelpers.AddSaturateUnsigned64),
        Helped(IrIntrinsicOp.SubSaturate, kind, "{h}({0}, {1})", IsSigned(kind) ? CSharpIntegerHelpers.SubSaturateSigned64 : CSharpIntegerHelpers.SubSaturateUnsigned64),
        Helped(IrIntrinsicOp.MulSaturate, kind, "{h}({0}, {1})", IsSigned(kind) ? CSharpIntegerHelpers.MulSaturateSigned64 : CSharpIntegerHelpers.MulSaturateUnsigned64)
      ]
      : IsSigned(kind)
      ? [
        Helped(IrIntrinsicOp.AddSaturate, kind, Bounded(kind, "(long){0} + {1}"), CSharpIntegerHelpers.Bounded),
        Helped(IrIntrinsicOp.SubSaturate, kind, Bounded(kind, "(long){0} - {1}"), CSharpIntegerHelpers.Bounded),
        Helped(IrIntrinsicOp.MulSaturate, kind, Bounded(kind, "(long){0} * {1}"), CSharpIntegerHelpers.Bounded)
      ]
      : [
        Helped(IrIntrinsicOp.AddSaturate, kind, $"({Type(kind)}){{h}}((ulong){{0}} + {{1}}, {Type(kind)}.MaxValue)", CSharpIntegerHelpers.BoundedAbove),
        Helped(IrIntrinsicOp.SubSaturate, kind, $"({Type(kind)}){{h}}((long){{0}} - {{1}}, 0L, {Type(kind)}.MaxValue)", CSharpIntegerHelpers.Bounded),
        Helped(IrIntrinsicOp.MulSaturate, kind, $"({Type(kind)}){{h}}((ulong){{0}} * {{1}}, {Type(kind)}.MaxValue)", CSharpIntegerHelpers.BoundedAbove)
      ])
  ];

  private static string Bounded(IrTypeKind kind, string exact) => $"({Type(kind)}){{h}}({exact}, {Type(kind)}.MinValue, {Type(kind)}.MaxValue)";

}
