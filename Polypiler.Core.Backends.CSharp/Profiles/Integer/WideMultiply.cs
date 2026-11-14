using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// <c>Math.BigMul(int, int)</c> has been there from the first framework on. The upper half of a
  /// product of up to 32 bits is the product in the next wider type shifted down; of 64 bits it is
  /// <c>Math.BigMul</c> with the lower half discarded from .NET 5, so the profile .NET 8, on, and a
  /// helper in four partial products before it.
  /// </summary>
  private static IEnumerable<CSharpCapability> WideMultiply => [
    Row(IrIntrinsicOp.BigMul, IrTypeKind.I32, CSharpProfile.Net20, Math + "BigMul", $"{Math}BigMul({{0}}, {{1}})"),
    .. _all.Where(kind => Width(kind) < 64).Select(kind => Written(IrIntrinsicOp.MulHigh, kind, kind switch {
      IrTypeKind.U16 => "(ushort)(((uint){0} * {1}) >> 16)",
      IrTypeKind.I32 => "(int)(((long){0} * {1}) >> 32)",
      IrTypeKind.U32 => "(uint)(((ulong){0} * {1}) >> 32)",
      _ => $"({Type(kind)})(({{0}} * {{1}}) >> {Digits(Width(kind))})"
    })),
    Row(IrIntrinsicOp.MulHigh, IrTypeKind.I64, CSharpProfile.Net80, Math + "BigMul", $"{Math}BigMul({{0}}, {{1}}, out _)", "{h}({0}, {1})", CSharpIntegerHelpers.MulHighSigned64),
    Row(IrIntrinsicOp.MulHigh, IrTypeKind.U64, CSharpProfile.Net80, Math + "BigMul", $"{Math}BigMul({{0}}, {{1}}, out _)", "{h}({0}, {1})", CSharpIntegerHelpers.MulHighUnsigned64)
  ];

}
