using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// The rotates. Of <c>uint</c> and <c>ulong</c> the table's first rows, <c>BitOperations</c>, are
  /// the rows; <c>int</c> and <c>long</c> rotate their bit pattern through those from .NET Core 3.1
  /// on, and the narrow types have their own <c>RotateLeft</c> from .NET 7 on. Before, each is the
  /// helper of the library for its width.
  /// </summary>
  private static IEnumerable<CSharpCapability> Rotate => [
    .. new[] { IrIntrinsicOp.RotateLeft, IrIntrinsicOp.RotateRight }.SelectMany(op => (IEnumerable<CSharpCapability>)[
      Library(op, IrTypeKind.I32, CSharpProfile.NetCoreApp31, Bits + op, $"(int){Bits}{op}({Word(IrTypeKind.I32)}, {{1}})"),
      Library(op, IrTypeKind.I64, CSharpProfile.NetCoreApp31, Bits + op, $"(long){Bits}{op}({Word(IrTypeKind.I64)}, {{1}})"),
      .. _narrow.Select(kind => Library(op, kind, CSharpProfile.Net80, $"{Type(kind)}.{op}", $"{Type(kind)}.{op}({{0}}, {{1}})"))
    ])
  ];

}
