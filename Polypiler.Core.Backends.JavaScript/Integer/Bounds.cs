using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The bounds of integers. A number is ordered by <c>Math.min</c> and <c>Math.max</c>, a
    /// <c>BigInt</c> and a pair by a helper. The magnitude traps on the smallest signed value, of an
    /// unsigned value it is the value; the one that wraps is <c>Math.abs</c> cut to the type. The
    /// clamp compares numbers and <c>BigInt</c>s alike, and pairs with their signedness.
    /// </summary>
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (JsExpression, bool)>)> BoundForms => [
      (IrIntrinsicOp.Min, (writer, intrinsic) => (Invoke("Math.min", [.. intrinsic.Arguments.Select(writer.Text)]), true)),
      (IrIntrinsicOp.Max, (writer, intrinsic) => (Invoke("Math.max", [.. intrinsic.Arguments.Select(writer.Text)]), true)),
      (IrIntrinsicOp.Abs, (writer, intrinsic) => writer.Expression(intrinsic.Arguments[0])),
      (IrIntrinsicOp.AbsWrap, (writer, intrinsic) => writer.AbsWrap(intrinsic.Arguments[0])),
      (IrIntrinsicOp.Sign, (writer, intrinsic) => writer.Sign(intrinsic.Arguments[0]))
    ];

    /// <summary>The sign of up to 32 bits by <c>Math.sign</c>; below ECMAScript 2015 the library's helper stands in.</summary>
    private (JsExpression, bool) Sign(IrExpr value) {
      this.Native("Math.sign");
      return (Invoke("Math.sign", this.Text(value)), true);
    }

    /// <summary>The magnitude that wraps: an unsigned value itself, a signed one of up to 32 bits <c>Math.abs</c> cut to its type.</summary>
    private (JsExpression, bool) AbsWrap(IrExpr value)
      => value.Type.IsSigned ? Cut(value.Type, Invoke("Math.abs", this.Text(value))) : this.Expression(value);

  }

}
