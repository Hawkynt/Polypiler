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
      (IrIntrinsicOp.LeadingZeroCount, (writer, intrinsic) => writer.LeadingZeroCount(intrinsic.Arguments[0])),
      (IrIntrinsicOp.Log2, (writer, intrinsic) => writer.Log2(intrinsic.Arguments[0]))
    ];

    /// <summary>The leading zeros of up to 32 bits by <c>Math.clz32</c> of the pattern, less the bits above a narrower type.</summary>
    private (JsExpression, bool) LeadingZeroCount(IrExpr value) {
      var count = Invoke("Math.clz32", this.Pattern(value));
      return value.Type.Bits == 32 ? (count, true) : (Infix(count, "-", Number((32 - value.Type.Bits).ToString(CultureInfo.InvariantCulture))), false);
    }

    /// <summary>The position of the highest set bit of up to 32 bits: 31 less the leading zeros of the pattern with its lowest bit set, which makes it 0 for 0.</summary>
    private (JsExpression, bool) Log2(IrExpr value)
      => (Infix(Number("31"), "-", Invoke("Math.clz32", Infix(value.Type.Bits == 32 ? this.Operand(value) : Parenthesized(this.Pattern(value)), "|", Number("1")))), false);

  }

}
