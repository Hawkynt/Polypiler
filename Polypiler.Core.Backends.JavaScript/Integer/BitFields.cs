using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// Bit fields and single bits: a helper that traps on a field or a position outside the width,
    /// told the width up to 32 bits; of 64 bits one on the <c>BigInt</c> or the halves.
    /// </summary>
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (string, bool)>)> BitFieldForms => [
      (IrIntrinsicOp.ExtractBits, (writer, intrinsic) => writer.Field(intrinsic, "$extract")),
      (IrIntrinsicOp.InsertBits, (writer, intrinsic) => writer.Field(intrinsic, "$insert")),
      (IrIntrinsicOp.TestBit, (writer, intrinsic) => writer.Field(intrinsic, "$testbit")),
      (IrIntrinsicOp.SetBit, (writer, intrinsic) => writer.Field(intrinsic, "$setbit")),
      (IrIntrinsicOp.ClearBit, (writer, intrinsic) => writer.Field(intrinsic, "$clearbit")),
      (IrIntrinsicOp.ToggleBit, (writer, intrinsic) => writer.Field(intrinsic, "$togglebit"))
    ];

    private (string, bool) Field(IrIntrinsic intrinsic, string helper) {
      var type = intrinsic.Arguments[0].Type;
      var call = type.IsLong ? this.Call(helper + "64", intrinsic).Item1 : this.Call(helper, intrinsic, Bits(type)).Item1;
      return intrinsic.Type == IrType.Bool ? (call, true) : this.Typed(type, call);
    }

  }

}
