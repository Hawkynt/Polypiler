using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// Saturating arithmetic, which no framework has as a method of the base library before the generic
  /// math of .NET 7. Up to 32 bits the exact result is computed in 64 bits and clamped to the range
  /// of the type; of 64 bits a helper per operation tells overflow apart.
  /// </summary>
  private static IEnumerable<CSharpCapability> Saturating => [
    .. _all.SelectMany(kind => new[] { IrIntrinsicOp.AddSaturate, IrIntrinsicOp.SubSaturate, IrIntrinsicOp.MulSaturate }.Select(op => Helped(op, kind)))
  ];

}
