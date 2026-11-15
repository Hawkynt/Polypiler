using System;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>
/// The inherent methods of the integer and float types, by the families of <c>core</c>: the
/// <c>wrapping_*</c> methods wrap and the <c>saturating_*</c> ones clamp whatever the overflow mode;
/// a <c>checked_*</c> method is its value and an overflow flag, consumed on the spot by
/// <c>unwrap</c>, <c>expect</c>, <c>unwrap_or</c>, <c>is_some</c> or <c>is_none</c>, and an
/// <c>overflowing_*</c> method the same pair read by <c>.0</c>, <c>.1</c> or a tuple <c>let</c>.
/// </summary>
internal sealed partial class FunctionLowering {

  private const string Wrapping = "wrapping_";
  private const string CheckedPrefix = "checked_";
  private const string OverflowingPrefix = "overflowing_";
  private const string Saturating = "saturating_";

  private IrExpr Method(MethodCallExpr call) {
    if (call.Receiver is MethodCallExpr { Name: var inner } checkedCall && inner.StartsWith(CheckedPrefix, StringComparison.Ordinal))
      return this.Consumer(call, checkedCall);

    var receiver = this._typed.TypeOf(call.Receiver);
    var values = this.Operands([call.Receiver, .. call.Arguments]);
    if (receiver.Kind == RKind.Float)
      return this.FloatMethod(call.Name, values[0], receiver.Ir);

    return this.IntegerMethod(call.Name, values, receiver);
  }

  private IrExpr FloatMethod(string name, IrExpr value, IrType type) {
    switch (name) {
      case "abs":
        return new IrIntrinsic(IrIntrinsicOp.Abs, type, [value]);
      case "sqrt":
        return new IrIntrinsic(IrIntrinsicOp.Sqrt, type, [value]);
      case "to_bits": {
        var bits = new IrIntrinsic(IrIntrinsicOp.FloatBits, type == IrType.F32 ? IrType.I32 : IrType.I64, [value]);
        return Cast(bits, type == IrType.F32 ? IrType.U32 : IrType.U64);
      }
      default:
        return new IrIntrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, [value]);
    }
  }

  private IrExpr IntegerMethod(string name, IrExpr[] values, RType receiver) {
    var type = receiver.Ir;
    var a = values[0];
    var b = values.Length > 1 ? values[1] : null;
    if (name.StartsWith(Wrapping, StringComparison.Ordinal))
      return this.Wrapped(name[Wrapping.Length..], a, b, receiver);

    if (name.StartsWith(Saturating, StringComparison.Ordinal))
      return this.Saturated(name[Saturating.Length..], a, b, receiver);

    switch (name) {
      case "abs":
        return new IrIntrinsic(this.Checked ? IrIntrinsicOp.Abs : IrIntrinsicOp.AbsWrap, type, [a]);
      case "pow":
        if (!this.Checked)
          return this.Wrapped("pow", a, b, receiver);

        a = this.Stable(a);
        b = this.Stable(b!);
        return new IrConditional(this.Flag("pow", a, b, receiver, false), RustTraps.Overflow(type, "attempt to multiply with overflow"), this.Wrapped("pow", a, b, receiver));
      case "signum":
        return Cast(new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [a]), type);
      case "abs_diff": {
        var unsigned = receiver.Unsigned.Ir;
        a = this.Stable(a);
        b = this.Stable(b!);
        return new IrConditional(
          Compare(IrBinaryOp.Greater, a, b),
          this.Arithmetic(IrBinaryOp.Subtract, Cast(a, unsigned), Cast(b, unsigned), unsigned, false),
          this.Arithmetic(IrBinaryOp.Subtract, Cast(b, unsigned), Cast(a, unsigned), unsigned, false));
      }
      case "min":
        return new IrIntrinsic(IrIntrinsicOp.Min, type, [a, b!]);
      case "max":
        return new IrIntrinsic(IrIntrinsicOp.Max, type, [a, b!]);
      case "clamp":
        return new IrIntrinsic(IrIntrinsicOp.Clamp, type, [a, b!, values[2]]);
      case "div_euclid":
        return new IrIntrinsic(IrIntrinsicOp.DivEuclid, type, [a, b!]);
      case "rem_euclid":
        return new IrIntrinsic(IrIntrinsicOp.ModEuclid, type, [a, b!]);
      case "rotate_left" or "rotate_right":
        return new IrIntrinsic(name == "rotate_left" ? IrIntrinsicOp.RotateLeft : IrIntrinsicOp.RotateRight, type, [a, Cast(b!, IrType.I32)]);
      case "swap_bytes":
        return new IrIntrinsic(IrIntrinsicOp.ByteSwap, type, [a]);
      case "reverse_bits":
        return new IrIntrinsic(IrIntrinsicOp.BitReverse, type, [a]);
      case "count_ones":
        return Cast(new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [a]), IrType.U32);
      case "count_zeros":
        return Cast(Binary(IrBinaryOp.Subtract, new IrConst(IrType.I32, type.Bits), new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [a])), IrType.U32);
      case "leading_zeros":
        return Cast(new IrIntrinsic(IrIntrinsicOp.LeadingZeroCount, IrType.I32, [a]), IrType.U32);
      default:
        return Cast(new IrIntrinsic(IrIntrinsicOp.TrailingZeroCount, IrType.I32, [a]), IrType.U32);
    }
  }

  /// <summary>An operation of the <c>wrapping_*</c> family: the result modulo 2^width; a zero divisor still traps.</summary>
  private IrExpr Wrapped(string op, IrExpr a, IrExpr? b, RType receiver) {
    var type = receiver.Ir;
    return op switch {
      "add" => this.Arithmetic(IrBinaryOp.Add, a, b!, type, false),
      "sub" => this.Arithmetic(IrBinaryOp.Subtract, a, b!, type, false),
      "mul" => this.Arithmetic(IrBinaryOp.Multiply, a, b!, type, false),
      "div" => this.WrappingDivision(IrBinaryOp.Divide, a, b!, type),
      "rem" => this.WrappingDivision(IrBinaryOp.Remainder, a, b!, type),
      "neg" => this.Negate(a, type, false),
      "shl" => this.Shift(true, a, b!, type, false),
      "shr" => this.Shift(false, a, b!, type, false),
      "abs" => new IrIntrinsic(IrIntrinsicOp.AbsWrap, type, [a]),
      _ => new IrCall(this._module.Power("wrapping", receiver), type, [a, b!])
    };
  }

  /// <summary>
  /// Whether an operation overflows, of the <c>checked_*</c> family (<paramref name="checkedDivision"/>:
  /// a zero divisor is a failure too) or of the <c>overflowing_*</c> family. The operands are stable.
  /// </summary>
  private IrExpr Flag(string op, IrExpr a, IrExpr? b, RType receiver, bool checkedDivision) {
    var type = receiver.Ir;
    IrExpr MinimumByMinusOne() => receiver.IsSigned ? And(Equal(a, Minimum(type)), Equal(b!, new IrConst(type, -1))) : Truth(false);
    return op switch {
      "add" => new IrIntrinsic(IrIntrinsicOp.AddOverflows, IrType.Bool, [a, b!]),
      "sub" => new IrIntrinsic(IrIntrinsicOp.SubOverflows, IrType.Bool, [a, b!]),
      "mul" => new IrIntrinsic(IrIntrinsicOp.MulOverflows, IrType.Bool, [a, b!]),
      "neg" => receiver.IsSigned ? Equal(a, Minimum(type)) : Compare(IrBinaryOp.NotEqual, a, new IrConst(type, 0)),
      "abs" => Equal(a, Minimum(type)),
      "shl" or "shr" => Compare(IrBinaryOp.GreaterOrEqual, b!, new IrConst(IrType.U32, type.Bits)),
      "pow" => new IrCall(this._module.Power("overflows", receiver), IrType.Bool, [a, b!]),
      _ when checkedDivision => receiver.IsSigned ? Or(Equal(b!, new IrConst(type, 0)), MinimumByMinusOne()) : Equal(b!, new IrConst(type, 0)),
      _ => MinimumByMinusOne()
    };
  }

  /// <summary>The value and the flag of an <c>overflowing_*</c> call; the operands are evaluated here.</summary>
  private (IrExpr Value, IrExpr Flag) Overflowing(MethodCallExpr call) {
    var receiver = this._typed.TypeOf(call.Receiver);
    var values = this.Operands([call.Receiver, .. call.Arguments]);
    var a = this.Stable(values[0]);
    var b = values.Length > 1 ? this.Stable(values[1]) : null;
    var op = call.Name[OverflowingPrefix.Length..];
    return (this.Wrapped(op, a, b, receiver), this.Flag(op, a, b, receiver, false));
  }

  /// <summary>A <c>checked_*</c> call consumed by <c>unwrap</c>, <c>expect</c>, <c>unwrap_or</c>, <c>is_some</c> or <c>is_none</c>.</summary>
  private IrExpr Consumer(MethodCallExpr consumer, MethodCallExpr call) {
    var receiver = this._typed.TypeOf(call.Receiver);
    var type = receiver.Ir;
    var values = this.Operands([call.Receiver, .. call.Arguments, .. consumer.Name == "unwrap_or" ? consumer.Arguments : []]);
    var a = this.Stable(values[0]);
    var b = call.Arguments.Count > 0 ? this.Stable(values[1]) : null;
    var op = call.Name[CheckedPrefix.Length..];
    var flag = this.Flag(op, a, b, receiver, true);
    // The value is computed only where the flag says it exists: a checked division by zero never divides.
    IrExpr Value() => this.Wrapped(op, a, b, receiver);
    return consumer.Name switch {
      "is_some" => Not(flag),
      "is_none" => flag,
      "unwrap" => new IrConditional(flag, RustTraps.Panic(type, "called `Option::unwrap()` on a `None` value"), Value()),
      "expect" => new IrConditional(flag, RustTraps.Panic(type, ((StringLiteral)consumer.Arguments[0]).Value), Value()),
      _ => new IrConditional(flag, this.Stable(values[^1], "fallback"), Value())
    };
  }

  /// <summary>An operation of the <c>saturating_*</c> family: the nearest value of the type where the result lies outside.</summary>
  private IrExpr Saturated(string op, IrExpr a, IrExpr? b, RType receiver) {
    var type = receiver.Ir;
    switch (op) {
      case "add":
        return new IrIntrinsic(IrIntrinsicOp.AddSaturate, type, [a, b!]);
      case "sub":
        return new IrIntrinsic(IrIntrinsicOp.SubSaturate, type, [a, b!]);
      case "mul":
        return new IrIntrinsic(IrIntrinsicOp.MulSaturate, type, [a, b!]);
      case "neg":
        return new IrIntrinsic(IrIntrinsicOp.SubSaturate, type, [new IrConst(type, 0), a]);
      case "abs":
        a = this.Stable(a);
        return new IrConditional(Equal(a, Minimum(type)), Maximum(type), new IrIntrinsic(IrIntrinsicOp.AbsWrap, type, [a]));
      case "div":
        if (!receiver.IsSigned)
          return this.WrappingDivision(IrBinaryOp.Divide, a, b!, type);

        a = this.Stable(a);
        b = this.Stable(b!);
        return new IrConditional(And(Equal(a, Minimum(type)), Equal(b, new IrConst(type, -1))), Maximum(type), this.WrappingDivision(IrBinaryOp.Divide, a, b, type));
      default: {
        a = this.Stable(a);
        b = this.Stable(b!);
        // An overflowing power of a negative base with an odd exponent is negative: it saturates to MIN.
        IrExpr limit = receiver.IsSigned
          ? new IrConditional(
            And(Compare(IrBinaryOp.Less, a, new IrConst(type, 0)), Compare(IrBinaryOp.Equal, Binary(IrBinaryOp.And, b, new IrConst(IrType.U32, 1)), new IrConst(IrType.U32, 1))),
            Minimum(type),
            Maximum(type))
          : Maximum(type);
        return new IrConditional(this.Flag("pow", a, b, receiver, false), limit, this.Wrapped("pow", a, b, receiver));
      }
    }
  }

}
