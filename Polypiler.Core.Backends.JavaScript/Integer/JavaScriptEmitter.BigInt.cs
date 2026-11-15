using System.Globalization;
using System.Numerics;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The arbitrary-precision integer is a <c>BigInt</c> of ECMAScript 2020, whose operators are the
    /// IR's: exact, the division truncating, the bitwise ones on the infinite two's complement, a shift
    /// by a negative count the other way. A level without <c>BigInt</c> has no form of it: once per
    /// module, an <see cref="Inexpressible"/> diagnostic says so.
    /// </summary>
    private void RequireBigInt() {
      if (!this.Native("BigInt") && this._refused.Add("arbitrary-precision integer"))
        this._problems.Add(InexpressibleDescriptor.At(
          $"'BigInt' (the arbitrary-precision integer, a Python int no fixed width holds) cannot be expressed in JavaScript profile '{this._profile.LevelName}'"));
    }

    /// <summary>A constant of the arbitrary-precision integer: its digits with the suffix <c>n</c>, a negative one negated.</summary>
    private (JsExpression Node, bool Atomic) BigLiteral(BigInteger value) {
      this.RequireBigInt();
      return value.Sign < 0 ? (new JsUnary("-", new JsBigInt(-value)), false) : (new JsBigInt(value), true);
    }

    /// <summary>Negation and the complement, which a <c>BigInt</c> has as the IR means them.</summary>
    private (JsExpression Node, bool Atomic) UnaryBig(IrUnary unary) {
      this.RequireBigInt();
      return (new JsUnary(unary.Op == IrUnaryOp.BitNot ? "~" : "-", this.Operand(unary.Operand)), false);
    }

    /// <summary>
    /// An operator on two arbitrary-precision integers, or on one and an <c>i32</c> count. The division
    /// and the remainder are the ones of the unsigned 64-bit helpers, which check the zero divisor and
    /// cut nothing; a count is a number made a <c>BigInt</c>.
    /// </summary>
    private (JsExpression Node, bool Atomic) BinaryBig(IrBinary binary) {
      this.RequireBigInt();
      switch (binary.Op) {
        case IrBinaryOp.Divide:
          return (this.Helper("$udiv64", binary.Left, binary.Right), true);
        case IrBinaryOp.Remainder:
          return (this.Helper("$urem64", binary.Left, binary.Right), true);
      }

      var left = this.Operand(binary.Left);
      JsExpression right = binary.Op is IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight
        ? binary.Right is IrConst { Value: >= 0 } count ? new JsBigInt(count.Value) : Invoke("BigInt", this.Text(binary.Right))
        : this.Operand(binary.Right);
      var sign = binary.Op switch {
        IrBinaryOp.Add => "+",
        IrBinaryOp.Subtract => "-",
        IrBinaryOp.Multiply => "*",
        IrBinaryOp.ShiftLeft => "<<",
        IrBinaryOp.ShiftRight => ">>",
        _ => Sign(binary.Op)
      };
      return (Infix(left, sign, right), false);
    }

    /// <summary>
    /// A conversion from or to the arbitrary-precision integer. To a fixed width that wraps it is
    /// <c>BigInt.asIntN</c> or <c>BigInt.asUintN</c> of the width, a number below 64 bits; one that
    /// traps checks the bounds with <c>$bigfit</c>. From a number it is <c>BigInt</c>, from a 64-bit
    /// integer, a <c>BigInt</c> already, nothing. To <c>f64</c> it is <c>Number</c>, which rounds to
    /// nearest; from a float it is <c>$f2lc</c> with bounds no finite number reaches, which throws the
    /// overflow for a NaN and an infinity.
    /// </summary>
    private (JsExpression Node, bool Atomic) ConvertBig(IrConvert conversion) {
      this.RequireBigInt();
      var (from, to) = (conversion.Operand.Type, conversion.Type);
      if (to.IsBigInt)
        return from.IsFloat ? (this.Helper("$f2lc", [this.Text(conversion.Operand), Number("-1.7976931348623157e308"), Id("Infinity")]), true)
          : from.IsLong ? this.Expression(conversion.Operand)
          : (Invoke("BigInt", this.Text(conversion.Operand)), true);

      if (to.IsFloat)
        return (Invoke("Number", this.Text(conversion.Operand)), true);

      var bits = Number(to.Bits.ToString(CultureInfo.InvariantCulture));
      JsExpression big = conversion.Contract == IrConversion.Trap
        ? this.Helper("$bigfit", [this.Text(conversion.Operand), this.BigLiteral((BigInteger)IrOperators.Minimum(to)).Node, this.BigLiteral((BigInteger)IrOperators.Maximum(to)).Node])
        : Invoke(to.IsSigned ? "BigInt.asIntN" : "BigInt.asUintN", bits, this.Text(conversion.Operand));
      return to.IsLong ? (big, true) : (Invoke("Number", big), true);
    }

  }

}
