using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The full product of two 32-bit integers is a <c>BigInt</c> product, or the product of two
    /// pairs, sign-extended for <c>i32</c>. The upper half of a product of up to 16 bits is the exact
    /// product of two numbers divided by 2^width and rounded down; of 32 bits it takes a helper in
    /// 16-bit digits, as the product is beyond 2^53; of 64 bits it is the <c>BigInt</c> product
    /// shifted down, or the upper digits of the product of two pairs.
    /// </summary>
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (JsExpression, bool)>)> WideMultiplyForms => [
      (IrIntrinsicOp.BigMul, (writer, intrinsic) => writer.BigMul(intrinsic)),
      (IrIntrinsicOp.MulHigh, (writer, intrinsic) => writer.MulHigh(intrinsic))
    ];

    /// <summary>The full product of two 32-bit integers as the product of their <c>BigInt</c>s; on the halves it is the library's.</summary>
    private (JsExpression, bool) BigMul(IrIntrinsic intrinsic)
      => (Infix(Invoke("BigInt", this.Text(intrinsic.Arguments[0])), "*", Invoke("BigInt", this.Text(intrinsic.Arguments[1]))), false);

    /// <summary>The upper half of a product of 8 or 16 bits, exact as a number, or of 64 bits as a <c>BigInt</c>; of 32 bits and on the halves it is the library's.</summary>
    private (JsExpression, bool) MulHigh(IrIntrinsic intrinsic) {
      var (left, right) = (intrinsic.Arguments[0], intrinsic.Arguments[1]);
      var type = left.Type;
      if (type.IsLong)
        return Cut64(type, Infix(Infix(this.Operand(left), "*", this.Operand(right)), ">>", new JsBigInt(64)));

      return Cut(type, Invoke("Math.floor", Infix(Infix(this.Operand(left), "*", this.Operand(right)), "/", Number((1 << type.Bits).ToString(CultureInfo.InvariantCulture)))));
    }

  }

}
