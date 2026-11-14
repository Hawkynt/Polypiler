using System;
using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// Saturating arithmetic. A sum or difference of up to 32 bits is exact in a number and a
    /// <c>BigInt</c> is exact anyway, so the exact result is clamped to the range. A product of 32
    /// bits beyond 2^53 is rounded, but it is then far outside the range on the same side, which is
    /// all the clamp asks. A pair of halves has a helper per operation.
    /// </summary>
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (string, bool)>)> SaturatingForms => [
      (IrIntrinsicOp.AddSaturate, (writer, intrinsic) => writer.Saturated(intrinsic, "+", "$addsat64")),
      (IrIntrinsicOp.SubSaturate, (writer, intrinsic) => writer.Saturated(intrinsic, "-", "$subsat64")),
      (IrIntrinsicOp.MulSaturate, (writer, intrinsic) => writer.Saturated(intrinsic, "*", "$mulsat64"))
    ];

    private (string, bool) Saturated(IrIntrinsic intrinsic, string sign, string halves) {
      var type = intrinsic.Type;
      if (this.AsHalves(type))
        return this.Call(halves, intrinsic, Signed(type));

      var (low, high) = this.Range(type);
      var exact = this.Helper("$sat", [$"{this.Operand(intrinsic.Arguments[0])} {sign} {this.Operand(intrinsic.Arguments[1])}", low, high]);
      return type.IsLong ? (exact, true) : Cut(type, exact);
    }

  }

}
