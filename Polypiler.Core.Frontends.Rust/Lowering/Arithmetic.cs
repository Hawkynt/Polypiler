using System;
using System.Globalization;
using System.Numerics;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>
/// The operators of Rust on the types of the IR. The IR computes on integers of 32 and 64 bits, as
/// C# does: an operation on a narrower type is computed on its widening - <c>i32</c> for the signed,
/// <c>u32</c> for the unsigned ones, where every sum, difference and product of two operands is exact -
/// and converted back, wrapping or trapping on a result outside the type.
/// </summary>
internal sealed partial class FunctionLowering {

  // ---------------------------------------------------------------- constants

  private static IrConst Integer(RType type, BigInteger value)
    => new(type.Ir, type.Ir == IrType.U64 ? unchecked((long)(ulong)(value & ulong.MaxValue)) : (long)value);

  private static IrConst Integer(IrType type, BigInteger value)
    => new(type, type == IrType.U64 ? unchecked((long)(ulong)(value & ulong.MaxValue)) : (long)value);

  /// <summary>A float literal, rounded to nearest even from its decimal digits in its own format.</summary>
  private static IrConst Float(RType type, string text) {
    var digits = text.EndsWith('.') ? text + "0" : text;
    return type.Ir == IrType.F32
      ? new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, float.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture)))
      : new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture)));
  }

  private static IrConst FloatConstant(IrType type, double value) => new(type, IrOperators.Bits(type, value));

  private static IrConst Truth(bool value) => new(IrType.Bool, value ? 1 : 0);

  private static IrConst Minimum(IrType type) => new(type, (long)IrOperators.Minimum(type));

  private static IrConst Maximum(IrType type) => Integer(type, (BigInteger)IrOperators.Maximum(type));

  // ---------------------------------------------------------------- widening

  /// <summary>The type an integer operation is computed in: the type itself from 32 bits on.</summary>
  private static IrType Promoted(IrType type) => type.IsWideInteger || !type.IsInteger ? type : type.IsSigned ? IrType.I32 : IrType.U32;

  /// <summary>A value as a wider type: a constant directly, anything else by a conversion that cannot fail.</summary>
  private static IrExpr Widen(IrExpr value, IrType type)
    => value.Type == type ? value : value is IrConst constant ? new IrConst(type, constant.Value) : new IrConvert(type, value);

  private static IrExpr Narrow(IrExpr value, IrType type, IrConversion contract) => value.Type == type ? value : new IrConvert(type, value, contract);

  private static IrExpr Binary(IrBinaryOp op, IrExpr left, IrExpr right) => new IrBinary(op, left, right);

  private static IrExpr Not(IrExpr value) => value is IrConst constant ? Truth(constant.Value == 0) : new IrUnary(IrUnaryOp.LogicalNot, value);

  private static IrExpr And(IrExpr left, IrExpr right) => Binary(IrBinaryOp.LogicalAnd, left, right);

  private static IrExpr Or(IrExpr left, IrExpr right) => Binary(IrBinaryOp.LogicalOr, left, right);

  /// <summary>A comparison of two values of one type, narrow integers widened.</summary>
  private static IrExpr Compare(IrBinaryOp op, IrExpr left, IrExpr right) {
    var type = Promoted(left.Type);
    return Binary(op, Widen(left, type), Widen(right, type));
  }

  private static IrExpr Equal(IrExpr left, IrExpr right)
    => left.Type.IsString ? new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [left, right]) : Compare(IrBinaryOp.Equal, left, right);

  // ---------------------------------------------------------------- arithmetic

  /// <summary>
  /// <c>+ - * / %</c> on a type. With <paramref name="trap"/> an overflowing sum, difference or product
  /// panics; division and remainder trap on a zero divisor and on <c>MIN / -1</c> and <c>MIN % -1</c>
  /// whatever the mode. <paramref name="left"/> and <paramref name="right"/> are read once each, except
  /// that a narrow signed remainder reads both twice: they are stable then.
  /// </summary>
  private IrExpr Arithmetic(IrBinaryOp op, IrExpr left, IrExpr right, IrType type, bool trap) {
    if (type.IsFloat)
      return Binary(op, left, right);

    var trapping = trap && op is IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply;
    if (type.IsWideInteger) {
      var checkedOp = op switch { IrBinaryOp.Add => IrBinaryOp.AddChecked, IrBinaryOp.Subtract => IrBinaryOp.SubtractChecked, _ => IrBinaryOp.MultiplyChecked };
      return Binary(trapping ? checkedOp : op, left, right);
    }

    var wide = Promoted(type);
    if (op == IrBinaryOp.Remainder && type.IsSigned) {
      // The widened remainder of MIN by -1 is 0, which fits: Rust panics there.
      left = this.Stable(left);
      right = this.Stable(right);
      return new IrConditional(
        And(Equal(left, Minimum(type)), Equal(right, new IrConst(type, -1))),
        RustTraps.Overflow(type, "attempt to calculate the remainder with overflow"),
        Narrow(Binary(op, Widen(left, wide), Widen(right, wide)), type, IrConversion.Wrap));
    }

    // MIN / -1 of a narrow signed type is the one quotient that does not fit: the conversion traps.
    var contract = trapping || op == IrBinaryOp.Divide ? IrConversion.Trap : IrConversion.Wrap;
    return Narrow(Binary(op, Widen(left, wide), Widen(right, wide)), type, contract);
  }

  /// <summary><c>wrapping_div</c> and <c>wrapping_rem</c>: <c>MIN / -1</c> is <c>MIN</c>, <c>MIN % -1</c> is 0; a zero divisor still traps.</summary>
  private IrExpr WrappingDivision(IrBinaryOp op, IrExpr left, IrExpr right, IrType type) {
    if (!type.IsWideInteger)
      return Narrow(Binary(op, Widen(left, Promoted(type)), Widen(right, Promoted(type))), type, IrConversion.Wrap);

    if (!type.IsSigned)
      return Binary(op, left, right);

    left = this.Stable(left);
    right = this.Stable(right);
    return new IrConditional(
      Equal(right, new IrConst(type, -1)),
      op == IrBinaryOp.Divide ? this.Negate(left, type, false) : new IrConst(type, 0),
      Binary(op, left, right));
  }

  /// <summary>Unary minus: of a signed integer trapping or wrapping on <c>MIN</c>, of an unsigned one modulo 2^width (<c>wrapping_neg</c>).</summary>
  private IrExpr Negate(IrExpr value, IrType type, bool trap) {
    if (type.IsFloat)
      return new IrUnary(IrUnaryOp.Negate, value);

    if (!type.IsSigned)
      return Narrow(Binary(IrBinaryOp.Subtract, new IrConst(Promoted(type), 0), Widen(value, Promoted(type))), type, IrConversion.Wrap);

    if (type.IsWideInteger)
      return new IrUnary(trap ? IrUnaryOp.NegateChecked : IrUnaryOp.Negate, value);

    return Narrow(new IrUnary(IrUnaryOp.Negate, Widen(value, IrType.I32)), type, trap ? IrConversion.Trap : IrConversion.Wrap);
  }

  /// <summary><c>& | ^</c> on integers, or <c>!</c> when <paramref name="right"/> is <see langword="null"/>.</summary>
  private static IrExpr Bitwise(IrBinaryOp? op, IrExpr left, IrExpr? right, IrType type) {
    var wide = Promoted(type);
    var computed = op is { } binary ? Binary(binary, Widen(left, wide), Widen(right!, wide)) : new IrUnary(IrUnaryOp.BitNot, Widen(left, wide));
    return Narrow(computed, type, IrConversion.Wrap);
  }

  /// <summary>
  /// A shift by an amount of any integer type. Checked, an amount outside 0 to width - 1 - a negative
  /// one read as a large unsigned one - panics; unchecked, the amount is masked to the width. A right
  /// shift is arithmetic on a signed and logical on an unsigned type.
  /// </summary>
  private IrExpr Shift(bool left, IrExpr value, IrExpr amount, IrType type, bool trap) {
    var bits = type.Bits;
    var wide = Promoted(type);
    var amountType = Promoted(amount.Type);
    IrExpr Shifted(IrExpr count)
      => Narrow(Binary(left ? IrBinaryOp.ShiftLeft : IrBinaryOp.ShiftRight, Widen(value, wide), count), type, IrConversion.Wrap);

    // A constant amount is in range: the checker refuses one outside it (arithmetic_overflow).
    if (amount is IrConst constant)
      return Shifted(new IrConst(IrType.I32, (long)(IrOperators.Number(amount.Type, constant.Value) & (bits - 1))));

    if (!trap) {
      var masked = Binary(IrBinaryOp.And, Widen(amount, amountType), new IrConst(amountType, bits - 1));
      return Shifted(Narrow(masked, IrType.I32, IrConversion.Wrap));
    }

    value = this.Stable(value);
    amount = this.Stable(amount);
    var tooFar = Compare(IrBinaryOp.GreaterOrEqual, amount, new IrConst(amount.Type, bits));
    if (amount.Type.IsSigned)
      tooFar = Or(Compare(IrBinaryOp.Less, amount, new IrConst(amount.Type, 0)), tooFar);

    return new IrConditional(tooFar, RustTraps.Overflow(type, $"attempt to shift {(left ? "left" : "right")} with overflow"), Shifted(Narrow(Widen(amount, amountType), IrType.I32, IrConversion.Wrap)));
  }

  // ---------------------------------------------------------------- casts

  /// <summary>
  /// <c>as</c>: between integers the value modulo 2^width of the target, the source's sign extended;
  /// from a float to an integer truncated and saturated, NaN to 0; to a float rounded to nearest
  /// even; from <c>bool</c> 0 or 1.
  /// </summary>
  private static IrExpr Cast(IrExpr value, IrType target) {
    if (value.Type == target)
      return value;

    if (value.Type == IrType.Bool)
      return new IrConditional(value, new IrConst(target, 1), new IrConst(target, 0));

    if (value is IrConst constant && value.Type.IsInteger && target.IsInteger)
      return new IrConst(target, IrOperators.Wrap(target, constant.Value));

    return new IrConvert(target, value, value.Type.IsFloat && target.IsInteger ? IrConversion.Saturate : IrConversion.Wrap);
  }

  /// <summary>Float constants of a type: <c>MIN</c>, <c>MAX</c>, <c>NAN</c>, the infinities, <c>EPSILON</c>, <c>MIN_POSITIVE</c>.</summary>
  private static IrConst FloatLimit(IrType type, string name) {
    var single = type == IrType.F32;
    return FloatConstant(type, name switch {
      "MIN" => single ? float.MinValue : double.MinValue,
      "MAX" => single ? float.MaxValue : double.MaxValue,
      "NAN" => double.NaN,
      "INFINITY" => double.PositiveInfinity,
      "NEG_INFINITY" => double.NegativeInfinity,
      "EPSILON" => Math.ScaleB(1, single ? -23 : -52),
      _ => Math.ScaleB(1, single ? -126 : -1022)
    });
  }

}
