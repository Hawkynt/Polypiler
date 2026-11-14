using System;
using System.Collections.Generic;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// Carry and borrow of the unsigned types: the sum or difference with the incoming bit is exact in
    /// a number or a <c>BigInt</c>, its low word the cut to the type and its carry or borrow a
    /// comparison; a pair of halves has a helper per operation.
    /// </summary>
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (JsExpression, bool)>)> CarryForms => [
      (IrIntrinsicOp.AddCarry, (writer, intrinsic) => writer.Carried(intrinsic, "$adc64",
        (sum, _) => writer.Typed(intrinsic.Type, Infix(Infix(sum.Left, "+", sum.Right), "+", sum.Bit)))),
      (IrIntrinsicOp.CarryOut, (writer, intrinsic) => writer.Carried(intrinsic, "$adcout64",
        (sum, max) => (Infix(Infix(Infix(sum.Left, "+", sum.Right), "+", sum.Bit), ">", max), false))),
      (IrIntrinsicOp.SubBorrow, (writer, intrinsic) => writer.Carried(intrinsic, "$sbb64",
        (sum, _) => writer.Typed(intrinsic.Type, Infix(Infix(sum.Left, "-", sum.Right), "-", sum.Bit)))),
      (IrIntrinsicOp.BorrowOut, (writer, intrinsic) => writer.Carried(intrinsic, "$sbbout64",
        (sum, _) => (Infix(sum.Left, "<", Infix(sum.Right, "+", sum.Bit)), false)))
    ];

    private (JsExpression, bool) Carried(
      IrIntrinsic intrinsic, string halves, Func<(JsExpression Left, JsExpression Right, JsExpression Bit), JsExpression, (JsExpression, bool)> form) {
      var type = intrinsic.Arguments[0].Type;
      if (this.AsHalves(type))
        return this.Call(halves, intrinsic);

      JsExpression one = type.IsLong ? new JsBigInt(1) : Number("1");
      JsExpression zero = type.IsLong ? new JsBigInt(0) : Number("0");
      var bit = Parenthesized(new JsConditional(this.Operand(intrinsic.Arguments[2]), one, zero));
      return form((this.Operand(intrinsic.Arguments[0]), this.Operand(intrinsic.Arguments[1]), bit), this.Range(type).High);
    }

  }

}
