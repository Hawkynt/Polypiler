using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// The floor and Euclidean divisions, which no framework has: a helper on <c>long</c> for the signed
  /// types, told the smallest value of the type so that it traps on that value divided by -1, and of
  /// the unsigned types the division and the remainder C# has, through a helper so that a divisor the
  /// compiler sees to be 0 traps at run time as well.
  /// </summary>
  private static IEnumerable<CSharpCapability> Division => [
    .. _all.SelectMany(kind => new[] { IrIntrinsicOp.DivFloor, IrIntrinsicOp.ModFloor, IrIntrinsicOp.DivEuclid, IrIntrinsicOp.ModEuclid }.Select(op => Helped(op, kind)))
  ];

}
