using System;
using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The counts. Of up to 32 bits they are those of the pattern as a 32-bit number: the leading
    /// zeros of a narrow type less the bits above it, its trailing zeros stopped by a bit set just
    /// above its width; <c>Math.clz32</c> takes any number as its 32 bits. Of 64 bits the helpers
    /// take the unsigned <c>BigInt</c> or the halves.
    /// </summary>
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (string, bool)>)> CountForms => [
      (IrIntrinsicOp.PopCount, (writer, intrinsic) => writer.PopCount(intrinsic.Arguments[0])),
      (IrIntrinsicOp.LeadingZeroCount, (writer, intrinsic) => writer.LeadingZeroCount(intrinsic.Arguments[0])),
      (IrIntrinsicOp.TrailingZeroCount, (writer, intrinsic) => writer.TrailingZeroCount(intrinsic.Arguments[0])),
      (IrIntrinsicOp.Log2, (writer, intrinsic) => writer.Log2(intrinsic.Arguments[0])),
      (IrIntrinsicOp.Parity, (writer, intrinsic) => ($"{writer.PopCount(intrinsic.Arguments[0]).Text} & 1", false))
    ];

    /// <summary>The argument of a helper of 64 bits: the halves, or the unsigned <c>BigInt</c>.</summary>
    private string Wide(IrExpr value) => this.Halves ? this.Text(value) : this.Unsigned64(value);

    private (string Text, bool Atomic) PopCount(IrExpr value)
      => (value.Type.IsLong ? this.Helper("$pop64", [this.Wide(value)]) : this.Helper("$pop", [this.Pattern(value)]), true);

    private (string, bool) LeadingZeroCount(IrExpr value) {
      if (value.Type.IsLong)
        return (this.Helper("$clz64", [this.Wide(value)]), true);

      var count = this.Clz32(this.Pattern(value));
      return value.Type.Bits == 32 ? (count, true) : ($"{count} - {32 - value.Type.Bits}", false);
    }

    private (string, bool) TrailingZeroCount(IrExpr value) {
      if (value.Type.IsLong)
        return (this.Helper("$ctz64", [this.Wide(value)]), true);

      return (this.Helper("$ctz", [value.Type.Bits == 32 ? this.Text(value) : $"{this.Operand(value)} | 0x{1 << value.Type.Bits:X}"]), true);
    }

    /// <summary>The position of the highest set bit; the lowest bit set by hand makes 0 behave like 1, whose logarithm is the 0 that is wanted.</summary>
    private (string, bool) Log2(IrExpr value) {
      if (this.AsHalves(value.Type))
        return ($"63 - {this.Helper("$clz64", [this.Helper("$or64", [this.Text(value), One64])])}", false);

      if (value.Type.IsLong)
        return ($"63 - {this.Helper("$clz64", [(value.Type.IsSigned ? this.Unsigned64(value) : this.Operand(value)) + " | 1n"])}", false);

      return ($"31 - {this.Clz32((value.Type.Bits == 32 ? this.Operand(value) : $"({this.Pattern(value)})") + " | 1")}", false);
    }

  }

}
