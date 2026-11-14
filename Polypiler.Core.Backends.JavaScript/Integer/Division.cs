using System;
using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The floor and Euclidean divisions. Of an unsigned type they are the division and the remainder
    /// the emitter already writes; of a signed one a helper that traps as the division does, told the
    /// smallest value of the type up to 32 bits.
    /// </summary>
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (string, bool)>)> DivisionForms => [
      (IrIntrinsicOp.DivFloor, (writer, intrinsic) => writer.Divided(intrinsic, "$divf", "div")),
      (IrIntrinsicOp.ModFloor, (writer, intrinsic) => writer.Divided(intrinsic, "$modf", "rem")),
      (IrIntrinsicOp.DivEuclid, (writer, intrinsic) => writer.Divided(intrinsic, "$dive", "div")),
      (IrIntrinsicOp.ModEuclid, (writer, intrinsic) => writer.Divided(intrinsic, "$mode", "rem"))
    ];

    private (string, bool) Divided(IrIntrinsic intrinsic, string signed, string unsigned) {
      var type = intrinsic.Arguments[0].Type;
      if (!type.IsSigned)
        return this.Call($"$u{unsigned}{(type.IsLong ? "64" : "")}", intrinsic);

      return type.IsLong ? this.Call(signed + "64", intrinsic) : Cut(type, this.Helper(signed, [this.Text(intrinsic.Arguments[0]), this.Text(intrinsic.Arguments[1]), this.Range(type).Low]));
    }

  }

}
