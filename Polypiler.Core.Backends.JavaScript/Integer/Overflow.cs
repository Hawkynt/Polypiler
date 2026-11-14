using System;
using System.Collections.Generic;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The overflow tests: whether the exact result lies outside the range, which a number tells as
    /// the saturation does and a <c>BigInt</c> exactly; a pair of halves has a helper per operation.
    /// </summary>
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (JsExpression, bool)>)> OverflowForms => [
      (IrIntrinsicOp.AddOverflows, (writer, intrinsic) => writer.Overflows(intrinsic, "+", "$addovf64")),
      (IrIntrinsicOp.SubOverflows, (writer, intrinsic) => writer.Overflows(intrinsic, "-", "$subovf64")),
      (IrIntrinsicOp.MulOverflows, (writer, intrinsic) => writer.Overflows(intrinsic, "*", "$mulovf64"))
    ];

    private (JsExpression, bool) Overflows(IrIntrinsic intrinsic, string sign, string halves) {
      var type = intrinsic.Arguments[0].Type;
      if (this.AsHalves(type))
        return this.Call(halves, intrinsic, Signed(type));

      var (low, high) = this.Range(type);
      return (this.Helper("$outside", [Infix(this.Operand(intrinsic.Arguments[0]), sign, this.Operand(intrinsic.Arguments[1])), low, high]), true);
    }

  }

}
