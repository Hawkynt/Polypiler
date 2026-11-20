using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// The counts, through <c>BitOperations</c> from .NET Core 3.1 on and through the helpers of the
  /// <c>uint</c> and <c>ulong</c> rows before it. A narrower type is counted as the 32 bits of its
  /// pattern: its leading zeros are those less the bits above it, and its trailing zeros stop at a
  /// bit set just above its width, which also makes them the width for 0.
  /// </summary>
  private static IEnumerable<CSharpCapability> Counts => [
    .. _all.Where(kind => kind is not (IrTypeKind.U32 or IrTypeKind.U64)).SelectMany(kind => (IEnumerable<CSharpCapability>)[
      Count(IrIntrinsicOp.PopCount, kind, "PopCount", Word(kind), ""),
      Count(IrIntrinsicOp.LeadingZeroCount, kind, "LeadingZeroCount", Word(kind), Width(kind) < 32 ? $" - {Digits(32 - Width(kind))}" : ""),
      Count(IrIntrinsicOp.TrailingZeroCount, kind, "TrailingZeroCount", Width(kind) < 32 ? $"{Word(kind)} | 0x{1 << Width(kind):X}u" : Word(kind), ""),
      Count(IrIntrinsicOp.Log2, kind, "Log2", Word(kind), "")
    ]),
    .. _all.Select(kind => Count(IrIntrinsicOp.Parity, kind, "PopCount", Word(kind), " & 1")),
    // The ones are the zeros of the complement: of a narrower type shifted to the top first, so that
    // the bits above it, which the complement sets, are shifted out; at the bottom they stop the count.
    .. _all.Select(kind => Count(IrIntrinsicOp.LeadingOneCount, kind, "LeadingZeroCount", Width(kind) < 32 ? $"~({Word(kind)} << {Digits(32 - Width(kind))})" : $"~{Word(kind)}", "")),
    .. _all.Select(kind => Count(IrIntrinsicOp.TrailingOneCount, kind, "TrailingZeroCount", $"~{Word(kind)}", ""))
  ];

  private static CSharpCapability Count(IrIntrinsicOp op, IrTypeKind kind, string method, string argument, string correction)
    => Library(op, kind, CSharpProfile.NetCoreApp31, Bits + method, Corrected($"{Bits}{method}({argument})", correction));

  /// <summary>A call with the correction for the width, in parentheses where there is one.</summary>
  private static string Corrected(string call, string correction) => correction.Length == 0 ? call : $"({call}{correction})";

}
