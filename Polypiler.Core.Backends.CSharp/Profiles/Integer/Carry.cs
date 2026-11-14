using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// Carry and borrow of the unsigned types. Up to 32 bits the sum and the difference are computed in
  /// 64 bits, where the carry is the result above the largest value and the borrow a comparison; the
  /// low word of 64 bits is the sum or difference as it wraps around, and its carry and borrow need a
  /// helper, which reads the operands more than once.
  /// </summary>
  private static IEnumerable<CSharpCapability> Carry => [
    .. _unsigned.SelectMany(kind => Width(kind) == 64
      ? (IEnumerable<CSharpCapability>)[
        Written(IrIntrinsicOp.AddCarry, kind, "({0} + {1} + ({2} ? 1UL : 0UL))"),
        Helped(IrIntrinsicOp.CarryOut, kind, "{h}({0}, {1}, {2})", CSharpIntegerHelpers.CarryOut64),
        Written(IrIntrinsicOp.SubBorrow, kind, "({0} - {1} - ({2} ? 1UL : 0UL))"),
        Helped(IrIntrinsicOp.BorrowOut, kind, "{h}({0}, {1}, {2})", CSharpIntegerHelpers.BorrowOut64)
      ]
      : [
        Written(IrIntrinsicOp.AddCarry, kind, $"({Type(kind)})((ulong){{0}} + {{1}} + ({{2}} ? 1UL : 0UL))"),
        Written(IrIntrinsicOp.CarryOut, kind, $"((ulong){{0}} + {{1}} + ({{2}} ? 1UL : 0UL) > {Type(kind)}.MaxValue)"),
        Written(IrIntrinsicOp.SubBorrow, kind, $"({Type(kind)})((ulong){{0}} - {{1}} - ({{2}} ? 1UL : 0UL))"),
        Written(IrIntrinsicOp.BorrowOut, kind, "((ulong){0} < (ulong){1} + ({2} ? 1UL : 0UL))")
      ])
  ];

}
