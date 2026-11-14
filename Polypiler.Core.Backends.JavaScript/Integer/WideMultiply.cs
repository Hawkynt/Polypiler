using System;
using System.Collections.Generic;
using System.Linq;
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
    private static IEnumerable<(IrIntrinsicOp, Func<Writer, IrIntrinsic, (string, bool)>)> WideMultiplyForms => [
      (IrIntrinsicOp.BigMul, (writer, intrinsic) => writer.BigMul(intrinsic)),
      (IrIntrinsicOp.MulHigh, (writer, intrinsic) => writer.MulHigh(intrinsic))
    ];

    private (string, bool) BigMul(IrIntrinsic intrinsic) {
      var signed = intrinsic.Arguments[0].Type.IsSigned;
      if (this.Halves)
        return (this.Helper("$mul64", intrinsic.Arguments.Select(argument => signed ? this.Helper("$sx64", [this.Text(argument)]) : $"{{ hi: 0, lo: {this.Text(argument)} }}")), true);

      return ($"BigInt({this.Text(intrinsic.Arguments[0])}) * BigInt({this.Text(intrinsic.Arguments[1])})", false);
    }

    private (string, bool) MulHigh(IrIntrinsic intrinsic) {
      var (left, right) = (intrinsic.Arguments[0], intrinsic.Arguments[1]);
      var type = left.Type;
      if (this.AsHalves(type))
        return this.Call("$mulhi64", intrinsic, Signed(type));

      if (type.IsLong)
        return Cut64(type, $"{this.Operand(left)} * {this.Operand(right)} >> 64n");

      if (type.Bits == 32)
        return (this.Helper(type.IsSigned ? "$smulhi" : "$mulhi", [this.Text(left), this.Text(right)]), true);

      return Cut(type, $"Math.floor({this.Operand(left)} * {this.Operand(right)} / {1 << type.Bits})");
    }

  }

}
