using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// The Boolean functions: the two-input ones are C#'s operators in every profile, each operand
  /// written once and the result cast back to the type, which the promotion of a narrow type to
  /// <c>int</c> widened. They are spelled without <c>|</c>, by De Morgan, since an or with the
  /// sign-extended complement of a narrow signed type is warning CS0675. The bit select, the majority and the function of a truth table, which would
  /// read an operand twice, are a helper everywhere.
  /// </summary>
  private static IEnumerable<CSharpCapability> Boolean => [
    .. _all.SelectMany(kind => (IEnumerable<CSharpCapability>)[
      Written(IrIntrinsicOp.Nand, kind, $"({Type(kind)})~({{0}} & {{1}})"),
      Written(IrIntrinsicOp.Nor, kind, $"({Type(kind)})(~{{0}} & ~{{1}})"),
      Written(IrIntrinsicOp.Xnor, kind, $"({Type(kind)})~({{0}} ^ {{1}})"),
      Written(IrIntrinsicOp.AndNot, kind, $"({Type(kind)})({{0}} & ~{{1}})"),
      Written(IrIntrinsicOp.OrNot, kind, $"({Type(kind)})~(~{{0}} & {{1}})"),
      Written(IrIntrinsicOp.Implies, kind, $"({Type(kind)})~({{0}} & ~{{1}})"),
      Helped(IrIntrinsicOp.BitSelect, kind),
      Helped(IrIntrinsicOp.Majority, kind),
      Helped(IrIntrinsicOp.TernaryLogic, kind)
    ])
  ];

}
