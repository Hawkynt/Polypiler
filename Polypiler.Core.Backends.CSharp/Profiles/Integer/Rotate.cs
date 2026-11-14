using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// The rotates. Of <c>uint</c> and <c>ulong</c> the table's first rows, <c>BitOperations</c>, are
  /// the rows; <c>int</c> and <c>long</c> rotate their bit pattern through those from .NET Core 3.1
  /// on, and the narrow types have their own <c>RotateLeft</c> from .NET 7 on.
  /// </summary>
  private static IEnumerable<CSharpCapability> Rotate => [
    .. new[] { IrIntrinsicOp.RotateLeft, IrIntrinsicOp.RotateRight }.SelectMany(op => (IEnumerable<CSharpCapability>)[
      Wide(op, IrTypeKind.I32, op == IrIntrinsicOp.RotateLeft ? CSharpHelpers.RotateLeft32 : CSharpHelpers.RotateRight32),
      Wide(op, IrTypeKind.I64, op == IrIntrinsicOp.RotateLeft ? CSharpHelpers.RotateLeft64 : CSharpHelpers.RotateRight64),
      .. _narrow.Select(kind => Row(op, kind, CSharpProfile.Net80, $"{Type(kind)}.{op}", $"{Type(kind)}.{op}({{0}}, {{1}})",
        $"({Type(kind)}){{h}}({Pattern(kind)}, {{1}}, {Digits(Width(kind))})",
        op == IrIntrinsicOp.RotateLeft ? CSharpIntegerHelpers.RotateLeftBits : CSharpIntegerHelpers.RotateRightBits))
    ])
  ];

  private static CSharpCapability Wide(IrIntrinsicOp op, IrTypeKind kind, CSharpHelper helper)
    => Row(op, kind, CSharpProfile.NetCoreApp31, Bits + op, $"({Type(kind)}){Bits}{op}({Word(kind)}, {{1}})", $"({Type(kind)}){{h}}({Word(kind)}, {{1}})", helper);

}
