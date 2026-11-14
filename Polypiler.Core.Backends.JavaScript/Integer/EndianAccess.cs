using System;
using System.Collections.Generic;
using System.Globalization;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// Endian loads and stores on an array of bytes, typed or plain, through a helper that checks the
    /// whole range first: of up to 32 bits on a number built from the bytes, of 64 bits on a
    /// <c>BigInt</c> or the halves. A store gets the value after the array and the index, as the IR
    /// evaluates them, and writes nothing when the range does not fit.
    /// </summary>
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (string, bool)>)> EndianForms => [
      (IrIntrinsicOp.LoadLittle, (writer, intrinsic) => writer.Load(intrinsic, "true")),
      (IrIntrinsicOp.LoadBig, (writer, intrinsic) => writer.Load(intrinsic, "false")),
      (IrIntrinsicOp.StoreLittle, (writer, intrinsic) => writer.Store(intrinsic, "true")),
      (IrIntrinsicOp.StoreBig, (writer, intrinsic) => writer.Store(intrinsic, "false"))
    ];

    private static string Size(IrType type) => (type.Bits / 8).ToString(CultureInfo.InvariantCulture);

    private (string, bool) Load(IrIntrinsic intrinsic, string little)
      => intrinsic.Type.IsLong
        ? this.Typed(intrinsic.Type, this.Call("$load64", intrinsic, little).Item1)
        : Cut(intrinsic.Type, this.Call("$load", intrinsic, Size(intrinsic.Type), little).Item1);

    private (string, bool) Store(IrIntrinsic intrinsic, string little) {
      var type = intrinsic.Arguments[2].Type;
      return type.IsLong ? this.Call("$store64", intrinsic, little) : this.Call("$store", intrinsic, Size(type), little);
    }

  }

}
