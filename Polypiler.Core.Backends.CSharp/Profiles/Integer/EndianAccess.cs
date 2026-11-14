using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// Endian loads are <c>BinaryPrimitives.Read…</c> on a span of the bytes from .NET Standard 2.1 on,
  /// whose constructor throws an <c>ArgumentOutOfRangeException</c> - the bounds trap - for a range
  /// outside the array. A store is a helper in every profile: the span of
  /// <c>BinaryPrimitives.Write…</c> would be made, and its range checked, before the value is
  /// evaluated, where the IR evaluates every argument first.
  /// </summary>
  private static IEnumerable<CSharpCapability> EndianAccess => [
    .. _multibyte.SelectMany(kind => (IEnumerable<CSharpCapability>)[
      Load(IrIntrinsicOp.LoadLittle, kind, "LittleEndian", "true"),
      Load(IrIntrinsicOp.LoadBig, kind, "BigEndian", "false"),
      Helped(IrIntrinsicOp.StoreLittle, kind, $"{{h}}({{0}}, {{1}}, (ulong){{2}}, {Digits(Width(kind) / 8)}, true)", CSharpIntegerHelpers.Store),
      Helped(IrIntrinsicOp.StoreBig, kind, $"{{h}}({{0}}, {{1}}, (ulong){{2}}, {Digits(Width(kind) / 8)}, false)", CSharpIntegerHelpers.Store)
    ])
  ];

  private static CSharpCapability Load(IrIntrinsicOp op, IrTypeKind kind, string order, string little) {
    var method = $"{Binary}Read{Name(kind)}{order}";
    var size = Digits(Width(kind) / 8);
    return Row(op, kind, CSharpProfile.NetStandard21, method, $"{method}(new global::System.ReadOnlySpan<byte>({{0}}, {{1}}, {size}))",
      $"({Type(kind)}){{h}}({{0}}, {{1}}, {size}, {little})", CSharpIntegerHelpers.Load);
  }

  /// <summary>The name of an integer type in the .NET API, as <c>BinaryPrimitives</c> spells it in its methods.</summary>
  private static string Name(IrTypeKind kind) => kind switch {
    IrTypeKind.I16 => "Int16",
    IrTypeKind.U16 => "UInt16",
    IrTypeKind.I32 => "Int32",
    IrTypeKind.U32 => "UInt32",
    IrTypeKind.I64 => "Int64",
    _ => "UInt64"
  };

}
