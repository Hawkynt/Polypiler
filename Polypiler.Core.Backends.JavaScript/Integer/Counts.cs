using System;
using System.Collections.Generic;
using System.Globalization;
using Polypiler.Core.Backends.JavaScript.Syntax;
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
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (JsExpression, bool)>)> CountForms => [
      (IrIntrinsicOp.PopCount, (writer, intrinsic) => writer.PopCount(intrinsic.Arguments[0])),
      (IrIntrinsicOp.LeadingZeroCount, (writer, intrinsic) => writer.LeadingZeroCount(intrinsic.Arguments[0])),
      (IrIntrinsicOp.TrailingZeroCount, (writer, intrinsic) => writer.TrailingZeroCount(intrinsic.Arguments[0])),
      (IrIntrinsicOp.Log2, (writer, intrinsic) => writer.Log2(intrinsic.Arguments[0])),
      (IrIntrinsicOp.Parity, (writer, intrinsic) => (Infix(writer.PopCount(intrinsic.Arguments[0]).Node, "&", Number("1")), false))
    ];

    /// <summary>The argument of a helper of 64 bits: the halves, or the unsigned <c>BigInt</c>.</summary>
    private JsExpression Wide(IrExpr value) => this.Halves ? this.Text(value) : this.Unsigned64(value);

    private (JsExpression Node, bool Atomic) PopCount(IrExpr value)
      => (value.Type.IsLong ? this.Helper("$pop64", [this.Wide(value)]) : this.Helper("$pop", [this.Pattern(value)]), true);

    private (JsExpression, bool) LeadingZeroCount(IrExpr value) {
      if (value.Type.IsLong)
        return (this.Helper("$clz64", [this.Wide(value)]), true);

      var count = this.Clz32(this.Pattern(value));
      return value.Type.Bits == 32 ? (count, true) : (Infix(count, "-", Number((32 - value.Type.Bits).ToString(CultureInfo.InvariantCulture))), false);
    }

    private (JsExpression, bool) TrailingZeroCount(IrExpr value) {
      if (value.Type.IsLong)
        return (this.Helper("$ctz64", [this.Wide(value)]), true);

      return (this.Helper("$ctz", [value.Type.Bits == 32 ? this.Text(value) : Infix(this.Operand(value), "|", Number($"0x{1 << value.Type.Bits:X}"))]), true);
    }

    /// <summary>The position of the highest set bit; the lowest bit set by hand makes 0 behave like 1, whose logarithm is the 0 that is wanted.</summary>
    private (JsExpression, bool) Log2(IrExpr value) {
      if (this.AsHalves(value.Type))
        return (Infix(Number("63"), "-", this.Helper("$clz64", [this.Helper("$or64", [this.Text(value), One64])])), false);

      if (value.Type.IsLong)
        return (Infix(Number("63"), "-", this.Helper("$clz64", [Infix(value.Type.IsSigned ? this.Unsigned64(value) : this.Operand(value), "|", new JsBigInt(1))])), false);

      return (Infix(Number("31"), "-", this.Clz32(Infix(value.Type.Bits == 32 ? this.Operand(value) : Parenthesized(this.Pattern(value)), "|", Number("1")))), false);
    }

  }

}
