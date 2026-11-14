using System;
using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The rotates: the 32-bit helpers for <c>u32</c> and <c>i32</c>, which shift by the count as
    /// JavaScript masks it, one told the width for the narrow types, and the 64-bit helper on the
    /// unsigned <c>BigInt</c> or on the halves.
    /// </summary>
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (string, bool)>)> RotateForms => [
      (IrIntrinsicOp.RotateLeft, (writer, intrinsic) => writer.Rotate(intrinsic, "$rotl")),
      (IrIntrinsicOp.RotateRight, (writer, intrinsic) => writer.Rotate(intrinsic, "$rotr"))
    ];

    private (string, bool) Rotate(IrIntrinsic intrinsic, string helper) {
      var (value, count) = (intrinsic.Arguments[0], intrinsic.Arguments[1]);
      var type = value.Type;
      if (type.IsLong)
        return this.AsHalves(type) || !type.IsSigned
          ? (this.Helper(helper + "64", [this.Text(value), this.Text(count)]), true)
          : Cut64(type, this.Helper(helper + "64", [this.Unsigned64(value), this.Text(count)]));

      if (type.Bits == 32) {
        var rotated = this.Helper(helper, [this.Text(value), this.Text(count)]);
        return type.IsSigned ? ($"{rotated} | 0", false) : (rotated, true);
      }

      return Cut(type, this.Helper(helper + "n", [this.Text(value), this.Text(count), Bits(type)]));
    }

  }

}
