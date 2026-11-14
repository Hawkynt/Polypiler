using System;
using System.Collections.Generic;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The test for a power of two takes the value as it is - a negative one is never a power - and
    /// the halves with their signedness; the rounding up works on the pattern, and the cut to the type
    /// makes 2^width the 0 it wraps to.
    /// </summary>
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (JsExpression, bool)>)> PowerForms => [
      (IrIntrinsicOp.IsPow2, (writer, intrinsic) => {
        var value = intrinsic.Arguments[0];
        return (value.Type.IsLong
          ? writer.Helper("$ispow264", [writer.Text(value), Signed(value.Type)])
          : writer.Helper("$ispow2", [writer.Text(value)]), true);
      }),
      (IrIntrinsicOp.RoundUpPow2, (writer, intrinsic) => {
        var value = intrinsic.Arguments[0];
        return value.Type.IsLong
          ? writer.Typed(value.Type, writer.Helper("$rup264", [writer.Text(value)]))
          : Cut(value.Type, writer.Helper("$rup2", [writer.Text(value), Bits(value.Type)]));
      })
    ];

  }

}
