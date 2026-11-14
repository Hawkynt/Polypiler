using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// The overflow tests. Up to 32 bits the exact result is computed in 64 bits and compared with the
  /// range: by a helper for a signed type, which compares it twice, and as the comparison itself for
  /// an unsigned one, whose sum and product cannot fall below 0 and whose difference does so exactly
  /// when the subtrahend is the larger. Of 64 bits a helper per operation tells overflow apart.
  /// </summary>
  private static IEnumerable<CSharpCapability> Overflow => [
    .. _all.SelectMany(kind => (kind, Width(kind) == 64) switch {
      (IrTypeKind.I64, _) => (IEnumerable<CSharpCapability>)[
        Helped(IrIntrinsicOp.AddOverflows, kind, "{h}({0}, {1})", CSharpIntegerHelpers.AddOverflowsSigned64),
        Helped(IrIntrinsicOp.SubOverflows, kind, "{h}({0}, {1})", CSharpIntegerHelpers.SubOverflowsSigned64),
        Helped(IrIntrinsicOp.MulOverflows, kind, "{h}({0}, {1})", CSharpIntegerHelpers.MulOverflowsSigned64)
      ],
      (_, true) => [
        Helped(IrIntrinsicOp.AddOverflows, kind, "{h}({0}, {1})", CSharpIntegerHelpers.AddOverflowsUnsigned64),
        Written(IrIntrinsicOp.SubOverflows, kind, "({0} < {1})"),
        Helped(IrIntrinsicOp.MulOverflows, kind, "{h}({0}, {1})", CSharpIntegerHelpers.MulOverflowsUnsigned64)
      ],
      _ when IsSigned(kind) => [
        Helped(IrIntrinsicOp.AddOverflows, kind, Outside(kind, "(long){0} + {1}"), CSharpIntegerHelpers.Outside),
        Helped(IrIntrinsicOp.SubOverflows, kind, Outside(kind, "(long){0} - {1}"), CSharpIntegerHelpers.Outside),
        Helped(IrIntrinsicOp.MulOverflows, kind, Outside(kind, "(long){0} * {1}"), CSharpIntegerHelpers.Outside)
      ],
      _ => [
        Written(IrIntrinsicOp.AddOverflows, kind, $"((ulong){{0}} + {{1}} > {Type(kind)}.MaxValue)"),
        Written(IrIntrinsicOp.SubOverflows, kind, "({0} < {1})"),
        Written(IrIntrinsicOp.MulOverflows, kind, $"((ulong){{0}} * {{1}} > {Type(kind)}.MaxValue)")
      ]
    })
  ];

  private static string Outside(IrTypeKind kind, string exact) => $"{{h}}({exact}, {Type(kind)}.MinValue, {Type(kind)}.MaxValue)";

}
