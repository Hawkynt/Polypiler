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
  /// <para>
  /// A floating-point number is read by <c>BinaryPrimitives.Read{Single,Double}…</c> from .NET 5 on,
  /// the first of the profiles net8.0; before it the load is the one of the integer of its width and
  /// the bit cast of that - <c>BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32…)</c> from
  /// .NET Standard 2.1 on, the helpers before - and a store is always the store of the integer that
  /// <c>BitConverter.SingleToInt32Bits</c> or <c>DoubleToInt64Bits</c> makes of the number. Nothing
  /// reads the byte order of the host.
  /// </para>
  /// </summary>
  private static IEnumerable<CSharpCapability> EndianAccess => [
    .. _multibyte.SelectMany(kind => (IEnumerable<CSharpCapability>)[
      Load(IrIntrinsicOp.LoadLittle, kind, "LittleEndian", "true"),
      Load(IrIntrinsicOp.LoadBig, kind, "BigEndian", "false"),
      Helped(IrIntrinsicOp.StoreLittle, kind),
      Helped(IrIntrinsicOp.StoreBig, kind)
    ]),
    .. new[] { IrTypeKind.F32, IrTypeKind.F64 }.SelectMany(kind => (IEnumerable<CSharpCapability>)[
      FloatLoad(IrIntrinsicOp.LoadLittle, kind, "LittleEndian"),
      FloatLoad(IrIntrinsicOp.LoadBig, kind, "BigEndian"),
      FloatStore(IrIntrinsicOp.StoreLittle, kind),
      FloatStore(IrIntrinsicOp.StoreBig, kind)
    ])
  ];

  /// <summary>The integer type of the width of a floating-point type.</summary>
  private static IrType IntegerOf(IrType type) => type == IrType.F32 ? IrType.I32 : IrType.I64;

  private static CSharpCapability FloatLoad(IrIntrinsicOp op, IrTypeKind kind, string order) {
    var method = $"{Binary}Read{(kind == IrTypeKind.F32 ? "Single" : "Double")}{order}";
    return Row(op, kind, CSharpProfile.Net80, method, $"{method}(new global::System.ReadOnlySpan<byte>({{0}}, {{1}}, {(kind == IrTypeKind.F32 ? "4" : "8")}))")
      .WithRewrite(FloatLoaded).WithLibrary((node, calls) => calls.Legalize(FloatLoaded((IrIntrinsic)node)));
  }

  private static CSharpCapability FloatStore(IrIntrinsicOp op, IrTypeKind kind)
    => Row(op, kind, null, null, null).WithRewrite(FloatStored).WithLibrary((node, calls) => calls.Legalize(FloatStored((IrIntrinsic)node)));

  /// <summary>The load of a floating-point number as the bit cast of the load of the integer of its width.</summary>
  private static IrExpr FloatLoaded(IrIntrinsic node) => new IrIntrinsic(IrIntrinsicOp.BitsFloat, node.Type, [node with { Type = IntegerOf(node.Type) }]);

  /// <summary>The store of a floating-point number as the store of the integer its bits are.</summary>
  private static IrExpr FloatStored(IrIntrinsic node) => node with {
    Arguments = [node.Arguments[0], node.Arguments[1], new IrIntrinsic(IrIntrinsicOp.FloatBits, IntegerOf(node.Arguments[2].Type), [node.Arguments[2]])]
  };

  private static CSharpCapability Load(IrIntrinsicOp op, IrTypeKind kind, string order, string little) {
    var method = $"{Binary}Read{Name(kind)}{order}";
    var size = Digits(Width(kind) / 8);
    return Library(op, kind, CSharpProfile.NetStandard21, method, $"{method}(new global::System.ReadOnlySpan<byte>({{0}}, {{1}}, {size}))");
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
