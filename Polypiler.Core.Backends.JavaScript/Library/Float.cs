using System.Collections.Generic;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptLibrary {

  /// <summary>
  /// The floating-point operations ECMAScript has no function for: the rounding to an integer with
  /// ties to even and away from zero, built on <c>Math.round</c>; the sign that traps on a NaN; the
  /// truncation below ECMAScript 2015, which has <c>Math.trunc</c>; the clamp, which traps on bounds
  /// out of order; and the rounding to a number of decimal digits. A <c>f32</c> is worked on as the
  /// <c>double</c> it is, and every one of these results is a <c>f32</c> again.
  /// </summary>
  private static IEnumerable<(IrIntrinsicOp, Row)> FloatRows => [
    (IrIntrinsicOp.Sign, Floating(node => "SignOf")),
    (IrIntrinsicOp.RoundEven, Floating(node => "RoundEven")),
    (IrIntrinsicOp.RoundAway, Floating(node => "RoundAway")),
    (IrIntrinsicOp.Truncate, new Row((node, context) => IsFloat(node) && context.Replaces("Math.trunc"), (node, calls) => calls.Call("Truncate", node.Type, node.Arguments))),
    (IrIntrinsicOp.Clamp, Floating(node => node.Type == IrType.F32 ? "ClampF" : "Clamp")),
    (IrIntrinsicOp.RoundDigits, Floating(node => node.Type == IrType.F32 ? "RoundDigitsF" : "RoundDigits"))
  ];

  private static bool IsFloat(IrIntrinsic node) => node.Arguments[0].Type.IsFloat;

  /// <summary>A row for the floating-point nodes of an operation, which always take the helper the name gives.</summary>
  private static Row Floating(System.Func<IrIntrinsic, string> helper)
    => new((node, _) => IsFloat(node), (node, calls) => calls.Call(helper(node), node.Type, node.Arguments));

}
