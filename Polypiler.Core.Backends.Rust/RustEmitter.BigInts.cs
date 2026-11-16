using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Polypiler.Core.Backends.Rust.Helpers;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  /// <summary>The Rust type of the IR's arbitrary-precision integer, a struct the crate carries (<see cref="BigInts"/>).</summary>
  public const string BigIntType = BigInts.TypeName;

  private sealed partial class Writer {

    /// <summary>Whether the crate carries <see cref="BigIntType"/>: some variable, result or operation of the module has it.</summary>
    private bool _bigInts;

    /// <summary>The type of the arbitrary-precision integer, which brings the struct and its order into the crate.</summary>
    private string BigType() {
      this._bigInts = true;
      this.BigHelper(BigInts.Compare);
      return BigIntType;
    }

    /// <summary>A helper of the arbitrary-precision integer the crate carries, with the helpers it calls; its name.</summary>
    private string BigHelper(string name) {
      this._bigInts = true;
      foreach (var helper in BigInts.With(name))
        this.Spell(helper.Name, () => helper);

      return name;
    }

    /// <summary>The struct and its order, where the crate carries the type.</summary>
    private IReadOnlyList<RsItem> BigItems() => this._bigInts ? BigInts.TypeItems() : [];

    /// <summary>A call of a helper of the arbitrary-precision integer.</summary>
    private RsCall BigCall(string helper, IrType type, params IReadOnlyList<RsExpression> arguments)
      => new(new RsPath(this.BigHelper(helper)), arguments) { Type = type };

    /// <summary>
    /// A constant of the arbitrary-precision integer: <c>ply_big(5)</c> of an <c>i128</c> literal where
    /// one holds it, otherwise the decimal digits, <c>ply_big_parse("…")</c>.
    /// </summary>
    private RsExpression BigLiteral(BigInteger value) {
      var digits = value.ToString(CultureInfo.InvariantCulture);
      return BigInteger.Abs(value) < BigInteger.One << 127
        ? this.BigCall("ply_big", IrType.BigInt, new RsLiteral(digits))
        : this.BigCall("ply_big_parse", IrType.BigInt, new RsString(digits));
    }

    /// <summary>
    /// An arbitrary-precision integer where its value is moved into a place: a variable cloned, since it
    /// stays the variable's; anything else, the new value of an operation, as it is.
    /// </summary>
    private RsExpression BigValue(IrLocalRef reference) => new RsMethodCall(new RsPath(this._locals[reference.Local]), "clone", []) { Type = IrType.BigInt };

    /// <summary>An arbitrary-precision integer where a helper borrows it: <c>&amp;x</c> of a variable, the new value of an operation borrowed for the statement.</summary>
    private RsExpression BigOperand(IrExpr value) => value is IrLocalRef reference
      ? new RsReference(false, new RsPath(this._locals[reference.Local]) { Type = IrType.BigInt }) { Type = IrType.BigInt }
      : new RsReference(false, this.Operand(this.Expression(value), 15)) { Type = IrType.BigInt };

    /// <summary>An arbitrary-precision integer as a comparison reads it: a variable in place, anything else as its value; the order is the type's own.</summary>
    private RsExpression BigPlace(IrExpr value) => value is IrLocalRef reference
      ? new RsPath(this._locals[reference.Local]) { Type = IrType.BigInt }
      : this.Expression(value);

    /// <summary>Negation and the complement: <c>ply_big_neg(&amp;a)</c>, <c>ply_big_not(&amp;a)</c>, the complement <c>-a - 1</c> as the IR defines it.</summary>
    private RsExpression BigUnary(IrUnary unary)
      => this.BigCall(unary.Op == IrUnaryOp.BitNot ? "ply_big_not" : "ply_big_neg", IrType.BigInt, this.BigOperand(unary.Operand));

    /// <summary>
    /// An operator on arbitrary-precision integers: the arithmetic and the bitwise operators through
    /// their helpers, which borrow both operands; a shift by an <c>i32</c> count as
    /// <c>ply_big_shift(&amp;a, i64::from(n))</c>, a right shift by the negated count (a negative count
    /// shifts the other way); a comparison as the operator of the type's order.
    /// </summary>
    private RsExpression BigBinary(IrBinary binary) {
      switch (binary.Op) {
        case >= IrBinaryOp.Equal and <= IrBinaryOp.GreaterOrEqual:
          return this.Binary(this.BigPlace(binary.Left), Comparison(binary.Op), this.BigPlace(binary.Right), IrType.Bool);
        case IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight: {
          var right = binary.Op == IrBinaryOp.ShiftRight;
          RsExpression count = binary.Right is IrConst constant
            ? new RsLiteral((right ? -constant.Value : constant.Value).ToString(CultureInfo.InvariantCulture)) { Type = IrType.I64 }
            : new RsCall(new RsPath("i64::from"), [this.Expression(binary.Right)]) { Type = IrType.I64 };
          if (right && count is RsCall)
            count = new RsUnary("-", count) { Type = IrType.I64 };
          return this.BigCall("ply_big_shift", IrType.BigInt, this.BigOperand(binary.Left), count);
        }
      }

      var helper = binary.Op switch {
        IrBinaryOp.Add => "ply_big_add",
        IrBinaryOp.Subtract => "ply_big_sub",
        IrBinaryOp.Multiply => "ply_big_mul",
        IrBinaryOp.Divide => "ply_big_div",
        IrBinaryOp.Remainder => "ply_big_rem",
        IrBinaryOp.And => "ply_big_and",
        IrBinaryOp.Or => "ply_big_or",
        _ => "ply_big_xor"
      };
      return this.BigCall(helper, IrType.BigInt, this.BigOperand(binary.Left), this.BigOperand(binary.Right));
    }

    /// <summary>The operator of a comparison.</summary>
    private static string Comparison(IrBinaryOp op) => op switch {
      IrBinaryOp.Equal => "==",
      IrBinaryOp.NotEqual => "!=",
      IrBinaryOp.Less => "<",
      IrBinaryOp.LessOrEqual => "<=",
      IrBinaryOp.Greater => ">",
      _ => ">="
    };

    /// <summary>
    /// A conversion from or to the arbitrary-precision integer. From an integer <c>ply_big(i128::from(x))</c>,
    /// exact, a constant as its literal; from a floating-point number <c>ply_big_of_f64(x)</c>, which
    /// truncates and traps on a NaN and an infinity. To a fixed width that wraps the low 64 bits of the
    /// two's complement, <c>ply_big_low(&amp;a)</c>, cut by <c>as</c>; to one that traps
    /// <c>ply_big_fit(&amp;a, min, max) as T</c>; to <c>f64</c> the nearest number, <c>ply_big_to_f64(&amp;a)</c>.
    /// </summary>
    private RsExpression BigConvert(IrConvert convert) {
      var (source, target) = (convert.Operand.Type, convert.Type);
      if (target.IsBigInt) {
        if (source.IsFloat) {
          var number = this.Expression(convert.Operand);
          return this.BigCall("ply_big_of_f64", target, source == IrType.F32 ? new RsCall(new RsPath("f64::from"), [number]) { Type = IrType.F64 } : number);
        }

        if (convert.Operand is IrConst constant)
          return this.BigLiteral((BigInteger)IrOperators.Number(source, constant.Value));

        return this.BigCall("ply_big", target, new RsCall(new RsPath("i128::from"), [Suffixed(this.Expression(convert.Operand))]) { Type = IrType.I64 });
      }

      var operand = this.BigOperand(convert.Operand);
      if (target.IsFloat)
        return this.BigCall("ply_big_to_f64", target, operand);

      if (convert.Contract == IrConversion.Trap) {
        var bounds = new RsExpression[] {
          operand,
          new RsLiteral(IrOperators.Minimum(target).ToString(CultureInfo.InvariantCulture)),
          new RsLiteral(IrOperators.Maximum(target).ToString(CultureInfo.InvariantCulture))
        };
        return this.Cast(this.BigCall("ply_big_fit", IrType.I64, bounds), target);
      }

      var low = this.BigCall("ply_big_low", IrType.U64, operand);
      return target == IrType.U64 ? low with { Type = target } : this.Cast(low, target);
    }

  }

}
