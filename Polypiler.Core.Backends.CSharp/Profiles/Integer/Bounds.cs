using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// The bounds the rows for every type do not cover. <c>Math.Min</c>, <c>Math.Max</c>,
  /// <c>Math.Abs</c> and <c>Math.Sign</c> have an overload for every signed type, and the first two
  /// for every unsigned one. The magnitude of an unsigned value is the value and its sign a
  /// comparison. <c>Math.Clamp</c> throws an <c>ArgumentException</c> for crossed bounds where the
  /// IR traps with an invalid operation, so the clamp is a helper in every profile, as is the
  /// magnitude that wraps around - except between constant bounds in order, which cannot cross and
  /// make the clamp the minimum of a maximum, as the recognition rule O0123 finds it.
  /// </summary>
  private static IEnumerable<CSharpCapability> Bounds => [
    .. _all.Select(kind => Helped(IrIntrinsicOp.Clamp, kind, $"({Type(kind)}){{h}}({{0}}, {{1}}, {{2}})",
      IsSigned(kind) ? CSharpIntegerHelpers.ClampSigned : CSharpIntegerHelpers.ClampUnsigned)
      .WithTotal(new CSharpForm(null, Template: $"{Math}Min({Math}Max({{0}}, {{1}}), {{2}})"), Ordered)),
    .. _unsigned.Select(kind => Written(IrIntrinsicOp.Abs, kind, "{0}")),
    .. _all.Select(kind => IsSigned(kind)
      ? Helped(IrIntrinsicOp.AbsWrap, kind, $"({Type(kind)}){{h}}({{0}})", CSharpIntegerHelpers.AbsWrap)
      : Written(IrIntrinsicOp.AbsWrap, kind, "{0}")),
    .. _unsigned.Select(kind => Written(IrIntrinsicOp.Sign, kind, "({0} != 0 ? 1 : 0)"))
  ];

  /// <summary>Whether the bounds of a clamp are constants in order, so that it cannot trap.</summary>
  private static bool Ordered(IReadOnlyList<IrExpr> arguments)
    => arguments[1] is IrConst low && arguments[2] is IrConst high && IrOperators.Number(low.Type, low.Value) <= IrOperators.Number(high.Type, high.Value);

}
