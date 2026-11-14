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
    /// evaluates them, and writes nothing when the range does not fit. A floating-point number goes
    /// through a <c>DataView</c> of the array where the profile has one; elsewhere it is the integer
    /// of its width, made of its bits or into them by the helpers of the bit casts.
    /// </summary>
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (string, bool)>)> EndianForms => [
      (IrIntrinsicOp.LoadLittle, (writer, intrinsic) => writer.Load(intrinsic, "true")),
      (IrIntrinsicOp.LoadBig, (writer, intrinsic) => writer.Load(intrinsic, "false")),
      (IrIntrinsicOp.StoreLittle, (writer, intrinsic) => writer.Store(intrinsic, "true")),
      (IrIntrinsicOp.StoreBig, (writer, intrinsic) => writer.Store(intrinsic, "false"))
    ];

    private static string Size(IrType type) => (type.Bits / 8).ToString(CultureInfo.InvariantCulture);

    /// <summary>The integer type of the width of a floating-point type, whose bits it is read and written as without a <c>DataView</c>.</summary>
    private static IrType IntegerOf(IrType type) => type == IrType.F32 ? IrType.I32 : IrType.I64;

    private (string, bool) Load(IrIntrinsic intrinsic, string little) {
      var type = intrinsic.Type;
      if (type.IsFloat) {
        var single = type == IrType.F32;
        return this.Native(single ? ".getFloat32" : ".getFloat64")
          ? this.Call(single ? "$loadf32" : "$loadf64", intrinsic, little)
          : this.Expression(new IrIntrinsic(IrIntrinsicOp.BitsFloat, type, [intrinsic with { Type = IntegerOf(type) }]));
      }

      return type.IsLong
        ? this.Typed(type, this.Call("$load64", intrinsic, little).Item1)
        : Cut(type, this.Call("$load", intrinsic, Size(type), little).Item1);
    }

    private (string, bool) Store(IrIntrinsic intrinsic, string little) {
      var type = intrinsic.Arguments[2].Type;
      if (type.IsFloat) {
        var single = type == IrType.F32;
        if (this.Native(single ? ".setFloat32" : ".setFloat64"))
          return this.Call(single ? "$storef32" : "$storef64", intrinsic, little);

        var bits = new IrIntrinsic(IrIntrinsicOp.FloatBits, IntegerOf(type), [intrinsic.Arguments[2]]);
        return this.Expression(intrinsic with { Arguments = [intrinsic.Arguments[0], intrinsic.Arguments[1], bits] });
      }

      return type.IsLong ? this.Call("$store64", intrinsic, little) : this.Call("$store", intrinsic, Size(type), little);
    }

  }

}
