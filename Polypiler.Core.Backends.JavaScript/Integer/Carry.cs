using System;
using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// Carry and borrow of the unsigned types: the sum or difference with the incoming bit is exact in
    /// a number or a <c>BigInt</c>, its low word the cut to the type and its carry or borrow a
    /// comparison; a pair of halves has a helper per operation.
    /// </summary>
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (string, bool)>)> CarryForms => [
      (IrIntrinsicOp.AddCarry, (writer, intrinsic) => writer.Carried(intrinsic, "$adc64", (sum, _) => writer.Typed(intrinsic.Type, $"{sum.Left} + {sum.Right} + {sum.Bit}"))),
      (IrIntrinsicOp.CarryOut, (writer, intrinsic) => writer.Carried(intrinsic, "$adcout64", (sum, max) => ($"{sum.Left} + {sum.Right} + {sum.Bit} > {max}", false))),
      (IrIntrinsicOp.SubBorrow, (writer, intrinsic) => writer.Carried(intrinsic, "$sbb64", (sum, _) => writer.Typed(intrinsic.Type, $"{sum.Left} - {sum.Right} - {sum.Bit}"))),
      (IrIntrinsicOp.BorrowOut, (writer, intrinsic) => writer.Carried(intrinsic, "$sbbout64", (sum, _) => ($"{sum.Left} < {sum.Right} + {sum.Bit}", false)))
    ];

    private (string, bool) Carried(IrIntrinsic intrinsic, string halves, Func<(string Left, string Right, string Bit), string, (string, bool)> form) {
      var type = intrinsic.Arguments[0].Type;
      if (this.AsHalves(type))
        return this.Call(halves, intrinsic);

      var one = type.IsLong ? "1n" : "1";
      var zero = type.IsLong ? "0n" : "0";
      var bit = $"({this.Operand(intrinsic.Arguments[2])} ? {one} : {zero})";
      return form((this.Operand(intrinsic.Arguments[0]), this.Operand(intrinsic.Arguments[1]), bit), this.Range(type).High);
    }

  }

}
