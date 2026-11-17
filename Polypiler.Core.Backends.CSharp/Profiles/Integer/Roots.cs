using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>The integer square root: no framework has it, so it is the helper of the library in every profile (#198).</summary>
  private static IEnumerable<CSharpCapability> Roots => [.. _unsigned.Select(kind => Helped(IrIntrinsicOp.ISqrt, kind))];

}
