using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// The two 32-bit limbs of a <c>long</c>, which C# writes with a shift and the casts that keep the
  /// bits: the high limb taken out, and the <c>long</c> joined from a high and a low one.
  /// </summary>
  private static IEnumerable<CSharpCapability> Limbs => [
    Written(IrIntrinsicOp.HighWord, IrTypeKind.I64, "(uint)((ulong){0} >> 32)"),
    Written(IrIntrinsicOp.JoinWords, IrTypeKind.U32, "(long)(((ulong){0} << 32) | {1})")
  ];

}
