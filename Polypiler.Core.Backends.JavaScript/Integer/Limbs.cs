using System;
using System.Collections.Generic;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The two 32-bit limbs of a 64-bit integer. Below ECMAScript 2020 a 64-bit integer is the object
    /// <c>{ hi, lo }</c> of its limbs, so the high limb is a property and the integer of two limbs a
    /// literal; a <c>BigInt</c> is shifted and joined.
    /// </summary>
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (JsExpression, bool)>)> LimbForms => [
      (IrIntrinsicOp.HighWord, (writer, intrinsic) => writer.HighWord(intrinsic.Arguments[0])),
      (IrIntrinsicOp.JoinWords, (writer, intrinsic) => writer.JoinWords(intrinsic.Arguments[0], intrinsic.Arguments[1]))
    ];

    /// <summary>The high limb: the property of the pair, or the unsigned <c>BigInt</c> shifted down as a number.</summary>
    private (JsExpression, bool) HighWord(IrExpr value) {
      if (this.Halves)
        return (new JsMember(this.Operand(value), "hi"), true);

      return (Invoke("Number", Infix(this.Unsigned64(value), ">>", new JsBigInt(32))), true);
    }

    /// <summary>The integer of two limbs: the pair, or the <c>BigInt</c> of the high limb shifted up and the low one, as a signed one.</summary>
    private (JsExpression, bool) JoinWords(IrExpr high, IrExpr low) {
      if (this.Halves)
        return (Halves(this.Text(high), this.Text(low)), true);

      return Cut64(IrType.I64, Infix(Infix(Invoke("BigInt", this.Text(high)), "<<", new JsBigInt(32)), "|", Invoke("BigInt", this.Text(low))));
    }

  }

}
