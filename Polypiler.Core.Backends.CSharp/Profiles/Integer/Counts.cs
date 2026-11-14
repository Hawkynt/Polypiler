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
      Count(IrIntrinsicOp.PopCount, kind, "PopCount", Word(kind), "", Width(kind) == 64 ? CSharpHelpers.PopCount64 : CSharpHelpers.PopCount32),
      Count(IrIntrinsicOp.LeadingZeroCount, kind, "LeadingZeroCount", Word(kind), Width(kind) < 32 ? $" - {Digits(32 - Width(kind))}" : "",
        Width(kind) == 64 ? CSharpHelpers.LeadingZeroCount64 : CSharpHelpers.LeadingZeroCount32),
      Count(IrIntrinsicOp.TrailingZeroCount, kind, "TrailingZeroCount", Width(kind) < 32 ? $"{Word(kind)} | 0x{1 << Width(kind):X}u" : Word(kind), "",
        Width(kind) == 64 ? CSharpHelpers.TrailingZeroCount64 : CSharpHelpers.TrailingZeroCount32),
      Count(IrIntrinsicOp.Log2, kind, "Log2", Word(kind), "", Width(kind) == 64 ? CSharpHelpers.Log264 : CSharpHelpers.Log232)
    ]),
    .. _all.Select(kind => Count(IrIntrinsicOp.Parity, kind, "PopCount", Word(kind), " & 1", Width(kind) == 64 ? CSharpHelpers.PopCount64 : CSharpHelpers.PopCount32))
  ];

  /// <summary>A count of <c>BitOperations</c> on the pattern of the value, with a correction after it.</summary>
  private static CSharpCapability Count(IrIntrinsicOp op, IrTypeKind kind, string method, string argument, string correction, CSharpHelper helper)
    => Row(op, kind, CSharpProfile.NetCoreApp31, Bits + method, Corrected($"{Bits}{method}({argument})", correction), Corrected($"{{h}}({argument})", correction), helper);

  private static string Corrected(string call, string correction) => correction.Length == 0 ? call : $"({call}{correction})";

}
