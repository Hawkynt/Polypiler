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
    .. _all.SelectMany(kind => (IEnumerable<CSharpCapability>)[
      Divides(IrIntrinsicOp.DivFloor, kind, CSharpIntegerHelpers.FloorDivide, CSharpIntegerHelpers.DivideUnsigned),
      Divides(IrIntrinsicOp.ModFloor, kind, CSharpIntegerHelpers.FloorModulo, CSharpIntegerHelpers.RemainderUnsigned),
      Divides(IrIntrinsicOp.DivEuclid, kind, CSharpIntegerHelpers.EuclidDivide, CSharpIntegerHelpers.DivideUnsigned),
      Divides(IrIntrinsicOp.ModEuclid, kind, CSharpIntegerHelpers.EuclidModulo, CSharpIntegerHelpers.RemainderUnsigned)
    ])
  ];

  private static CSharpCapability Divides(IrIntrinsicOp op, IrTypeKind kind, CSharpHelper signed, CSharpHelper unsigned)
    => IsSigned(kind)
      ? Helped(op, kind, $"({Type(kind)}){{h}}({{0}}, {{1}}, {Type(kind)}.MinValue)", signed)
      : Helped(op, kind, $"({Type(kind)}){{h}}({{0}}, {{1}})", unsigned);

}
