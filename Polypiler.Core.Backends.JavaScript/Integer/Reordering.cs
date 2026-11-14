using System;
using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>The byte swap and the bit reversal: a helper told the width up to 32 bits, one on the <c>BigInt</c> or the halves of 64.</summary>
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (string, bool)>)> ReorderingForms => [
      (IrIntrinsicOp.ByteSwap, (writer, intrinsic) => writer.Reordered(intrinsic, "$bswap")),
      (IrIntrinsicOp.BitReverse, (writer, intrinsic) => writer.Reordered(intrinsic, "$brev"))
    ];

    private (string, bool) Reordered(IrIntrinsic intrinsic, string helper) {
      var value = intrinsic.Arguments[0];
      return value.Type.IsLong
        ? this.Typed(value.Type, this.Helper(helper + "64", [this.Text(value)]))
        : Cut(value.Type, this.Helper(helper, [this.Text(value), Bits(value.Type)]));
    }

  }

}
