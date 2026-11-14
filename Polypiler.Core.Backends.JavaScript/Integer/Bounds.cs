using System;
using System.Collections.Generic;
using System.Linq;
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
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (string, bool)>)> BoundForms => [
      (IrIntrinsicOp.Min, (writer, intrinsic) => writer.Ordered(intrinsic, "min")),
      (IrIntrinsicOp.Max, (writer, intrinsic) => writer.Ordered(intrinsic, "max")),
      (IrIntrinsicOp.Clamp, (writer, intrinsic) => writer.AsHalves(intrinsic.Type)
        ? writer.Call("$clamp64", intrinsic, Signed(intrinsic.Type))
        : writer.Call("$clamp", intrinsic)),
      (IrIntrinsicOp.Abs, (writer, intrinsic) => writer.Abs(intrinsic.Arguments[0], true)),
      (IrIntrinsicOp.AbsWrap, (writer, intrinsic) => writer.Abs(intrinsic.Arguments[0], false)),
      (IrIntrinsicOp.Sign, (writer, intrinsic) => writer.Sign(intrinsic.Arguments[0]))
    ];

    private (string, bool) Ordered(IrIntrinsic intrinsic, string which) {
      var type = intrinsic.Type;
      var all = string.Join(", ", intrinsic.Arguments.Select(this.Text));
      return (this.AsHalves(type) ? this.Helper((type.IsSigned ? "$s" : "$u") + which + "64", [all])
        : type.IsLong ? this.Helper($"${which}64", [all])
        : $"Math.{which}({all})", true);
    }

    private (string, bool) Abs(IrExpr value, bool trap) {
      var type = value.Type;
      if (!type.IsSigned)
        return this.Expression(value);

      if (!trap)
        return type.IsLong ? this.Typed(type, this.Helper("$absw64", [this.Text(value)])) : Cut(type, $"Math.abs({this.Text(value)})");

      return (this.AsHalves(type) ? this.Helper("$abs64", [this.Text(value)]) : this.Helper("$abs", [this.Text(value), this.Range(type).Low]), true);
    }

    private (string, bool) Sign(IrExpr value) {
      var text = this.Text(value);
      if (this.AsHalves(value.Type))
        return (this.Helper(value.Type.IsSigned ? "$sign64" : "$usign64", [text]), true);

      return (value.Type.IsLong ? this.Helper("$sign64", [text]) : this.Native("Math.sign") ? $"Math.sign({text})" : this.Helper("$sign", [text]), true);
    }

  }

}
