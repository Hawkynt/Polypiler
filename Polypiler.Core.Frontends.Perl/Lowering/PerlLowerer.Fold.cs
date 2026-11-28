using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Frontends.Perl.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl.Lowering;

internal sealed partial class PerlLowerer {

  /// <summary>A value perl computes while it compiles: a number, a string, a boolean (its <c>PL_sv_yes</c>/<c>PL_sv_no</c>) or undef.</summary>
  private sealed record FoldedValue(PerlNumeric? Number, string? Text, bool? Boolean) {

    public static FoldedValue Undef { get; } = new(null, null, null);

    public bool Undefined => this.Number is null && this.Text is null && this.Boolean is null;

    public bool Truth => this.Boolean ?? (this.Number is { } n ? n.Double != 0 : this.Text is { } t && t.Length > 0 && t != "0");

    public string AsString() => this.Text ?? (this.Boolean is { } b ? b ? "1" : "" : this.Number is { } n ? NumberText(n) : "");

  }

  private static string NumberText(PerlNumeric n) => n.Kind switch {
    PerlNumericKind.Integer => n.Integer.ToString(CultureInfo.InvariantCulture),
    PerlNumericKind.Unsigned => n.Unsigned.ToString(CultureInfo.InvariantCulture),
    _ => n.Double.ToString("G15", CultureInfo.InvariantCulture)
  };

  private IrExpr LowerFolded(FoldedValue value) {
    if (value.Number is { } number)
      return LowerNumber(number);
    if (value.Text is { } text)
      return this.LowerString(text);
    if (value.Boolean is { } boolean)
      return new IrConst(IrType.Bool, boolean ? 1 : 0);
    return new IrNone(IrType.Optional(IrType.I64));
  }

  /// <summary>
  /// The value of an expression perl folds into a constant while it compiles (perlop: constant
  /// folding): literals, the constants of <c>use constant</c> and constant subs, and the arithmetic,
  /// bitwise, string and comparison operators and the pure builtins applied to constants; <see langword="null"/>
  /// for anything perl leaves to run time.
  /// </summary>
  private FoldedValue? Fold(PerlExpr expression) {
    switch (expression) {
      case PerlNumber n:
        return new(n.Value, null, null);
      case PerlString s:
        return new(null, s.Value, null);
      case PerlList { Parenthesized: true, Items: [var only] }:
        return this.Fold(only);
      case PerlCall { Builtin: false, Arguments: null or { Count: 0 } } call when this.ConstantValue(call.Name) is { } constant:
        return constant;
      case PerlCall { Builtin: true } call:
        return this.FoldBuiltin(call);
      case PerlUnary { Operand: { } operand } unary:
        return this.Fold(operand) is { } value ? this.FoldUnary(unary.Operator, value) : null;
      case PerlBinary binary:
        if (this.Fold(binary.Left) is not { } left)
          return null;
        if (binary.Operator is "&&" or "and")
          return left.Truth ? this.Fold(binary.Right) : left;
        if (binary.Operator is "||" or "or")
          return left.Truth ? left : this.Fold(binary.Right);
        if (binary.Operator == "//")
          return left.Undefined ? this.Fold(binary.Right) : left;
        return this.Fold(binary.Right) is { } right ? this.FoldBinary(binary.Operator, left, right) : null;
      case PerlTernary ternary:
        return this.Fold(ternary.Condition) is { } condition ? this.Fold(condition.Truth ? ternary.Then : ternary.Else) : null;
      default:
        return null;
    }
  }

  private FoldedValue? ConstantValue(string name) {
    var qualified = name.Contains("::", StringComparison.Ordinal) ? name : $"{this._currentPackage}::{name}";
    if (!this._constants.TryGetValue(qualified, out var value))
      return null;
    return value is null ? FoldedValue.Undef : this.Fold(value);
  }

  private FoldedValue? FoldBuiltin(PerlCall call) {
    if (call.Arguments is not [var argument] || call.Name is not ("chr" or "ord" or "length" or "abs" or "int" or "lc" or "uc" or "lcfirst" or "ucfirst" or "undef"))
      return call is { Name: "undef", Arguments.Count: 0 } ? FoldedValue.Undef : null;
    if (call.Name == "undef" || this.Fold(argument) is not { } value)
      return null;
    switch (call.Name) {
      case "chr":
        return value.Number is { IsIntegral: true } code && code.Integer is >= 0 and <= 0x10FFFF
          ? new(null, code.Integer is >= 0xD800 and <= 0xDFFF ? ((char)code.Integer).ToString() : char.ConvertFromUtf32((int)code.Integer), null)
          : null;
      case "ord":
        return value.Text is { } text ? new(PerlNumeric.Of(text.Length == 0 ? 0L : char.ConvertToUtf32(text, 0)), null, null) : null;
      case "length":
        return value.Text is { } s ? new(PerlNumeric.Of((long)s.Length), null, null) : null;
      case "abs":
        return value.Number is { Kind: PerlNumericKind.Integer } i && i.Integer != long.MinValue ? new(PerlNumeric.Of(Math.Abs(i.Integer)), null, null)
          : value.Number is { Kind: PerlNumericKind.Float } f ? new(PerlNumeric.Of(Math.Abs(f.Double)), null, null) : null;
      case "int":
        return value.Number is { IsIntegral: true } ? value
          : value.Number is { } d && Math.Abs(d.Double) < 9.2e18 ? new(PerlNumeric.Of((long)Math.Truncate(d.Double)), null, null) : null;
      case "lc" or "uc" or "lcfirst" or "ucfirst":
        if (value.Text is not { } t || t.Any(c => c > 0x7F))
          return null;
        return new(null, call.Name switch {
          "lc" => t.ToLowerInvariant(),
          "uc" => t.ToUpperInvariant(),
          "lcfirst" => t.Length == 0 ? t : char.ToLowerInvariant(t[0]) + t[1..],
          _ => t.Length == 0 ? t : char.ToUpperInvariant(t[0]) + t[1..]
        }, null);
    }
    return null;
  }

  private FoldedValue? FoldUnary(string op, FoldedValue value) {
    switch (op) {
      case "!" or "not":
        return new(null, null, !value.Truth);
      case "+":
        return value;
      case "-":
        if (value.Number is { } n)
          return new(n.Kind switch {
            PerlNumericKind.Integer when n.Integer != long.MinValue => PerlNumeric.Of(-n.Integer),
            PerlNumericKind.Unsigned when n.Unsigned == 9223372036854775808UL => PerlNumeric.Of(long.MinValue),
            _ => PerlNumeric.Of(-n.Double)
          }, null, null);
        if (value.Text is { Length: > 0 } t && (char.IsLetter(t[0]) || t[0] == '_'))
          return new(null, "-" + t, null);
        return null;
      case "~":
        if (value.Number is { IsIntegral: true } bits)
          return new(this._integer ? PerlNumeric.Of(~bits.Integer) : PerlNumeric.OfUnsigned(~bits.Unsigned), null, null);
        return null;
      default:
        return null;
    }
  }

  private FoldedValue? FoldBinary(string op, FoldedValue left, FoldedValue right) {
    switch (op) {
      case ".":
        return left.Text is not null || right.Text is not null || left.Number is { IsIntegral: true } && right.Number is { IsIntegral: true }
          ? new(null, left.AsString() + right.AsString(), null)
          : null;
      case "x":
        return left.Text is { } repeated && right.Number is { IsIntegral: true } count && count.Integer < 1 << 16
          ? new(null, count.Integer <= 0 ? "" : string.Concat(Enumerable.Repeat(repeated, (int)count.Integer)), null)
          : null;
      case "eq" or "ne" or "lt" or "gt" or "le" or "ge" or "cmp":
        if (left.Text is null || right.Text is null)
          return null;
        var order = string.CompareOrdinal(left.Text, right.Text);
        return op == "cmp" ? new(PerlNumeric.Of((long)Math.Sign(order)), null, null) : new(null, null, op switch {
          "eq" => order == 0,
          "ne" => order != 0,
          "lt" => order < 0,
          "gt" => order > 0,
          "le" => order <= 0,
          _ => order >= 0
        });
    }
    if (left.Number is not { } a || right.Number is not { } b)
      return null;
    var integral = a.IsIntegral && b.IsIntegral;
    var x = Big(a);
    var y = Big(b);
    switch (op) {
      case "+" or "-" or "*" when integral: {
        var exact = op switch { "+" => x + y, "-" => x - y, _ => x * y };
        if (this._integer || exact >= long.MinValue && exact <= ulong.MaxValue)
          return Integral(exact, this._integer);
        return new(PerlNumeric.Of(op switch { "+" => a.Double + b.Double, "-" => a.Double - b.Double, _ => a.Double * b.Double }), null, null);
      }
      case "+" or "-" or "*":
        return new(PerlNumeric.Of(op switch { "+" => a.Double + b.Double, "-" => a.Double - b.Double, _ => a.Double * b.Double }), null, null);
      case "/":
        if (b.Double == 0)
          return null;
        if (this._integer && integral)
          return Integral(BigInteger.Divide(x, y), true);
        return integral && BigInteger.Remainder(x, y).IsZero ? Integral(x / y, false) : new(PerlNumeric.Of(a.Double / b.Double), null, null);
      case "%":
        if (!integral || y.IsZero)
          return null;
        if (this._integer)
          return Integral(BigInteger.Remainder(x, y), true);
        var remainder = BigInteger.Remainder(x, y);
        if (!remainder.IsZero && remainder.Sign != y.Sign)
          remainder += y;
        return Integral(remainder, false);
      case "**":
        if (integral && y.Sign >= 0 && y < 64) {
          var power = BigInteger.Pow(x, (int)y);
          if (power >= long.MinValue && power <= ulong.MaxValue)
            return Integral(power, false);
        }
        return new(PerlNumeric.Of(Math.Pow(a.Double, b.Double)), null, null);
      case "&" or "|" or "^" when integral:
        var ua = this._integer ? (ulong)a.Integer : a.Unsigned;
        var ub = this._integer ? (ulong)b.Integer : b.Unsigned;
        var bits = op switch { "&" => ua & ub, "|" => ua | ub, _ => ua ^ ub };
        return new(this._integer ? PerlNumeric.Of((long)bits) : PerlNumeric.OfUnsigned(bits), null, null);
      case "<<" or ">>" when integral && b.Integer is >= 0 and < 64:
        if (this._integer)
          return new(PerlNumeric.Of(op == "<<" ? a.Integer << (int)b.Integer : a.Integer >> (int)b.Integer), null, null);
        return new(PerlNumeric.OfUnsigned(op == "<<" ? a.Unsigned << (int)b.Integer : a.Unsigned >> (int)b.Integer), null, null);
      case "<" or "<=" or ">" or ">=" or "==" or "!=":
        var compare = integral ? x.CompareTo(y) : a.Double.CompareTo(b.Double);
        return new(null, null, op switch {
          "<" => compare < 0,
          "<=" => compare <= 0,
          ">" => compare > 0,
          ">=" => compare >= 0,
          "==" => compare == 0,
          _ => compare != 0
        });
      case "<=>":
        return new(PerlNumeric.Of((long)(integral ? x.CompareTo(y) : a.Double.CompareTo(b.Double))), null, null);
    }
    return null;
  }

  private static BigInteger Big(PerlNumeric n) => n.Kind switch {
    PerlNumericKind.Integer => n.Integer,
    PerlNumericKind.Unsigned => n.Unsigned,
    _ => BigInteger.Zero
  };

  /// <summary>An integer result as perl keeps it: an IV, a UV beyond, a binary64 beyond that; under <c>use integer</c> wrapped to 64 bits.</summary>
  private static FoldedValue Integral(BigInteger value, bool integer) {
    if (integer)
      return new(PerlNumeric.Of(unchecked((long)(ulong)(value & ulong.MaxValue))), null, null);
    if (value >= long.MinValue && value <= long.MaxValue)
      return new(PerlNumeric.Of((long)value), null, null);
    if (value >= 0 && value <= ulong.MaxValue)
      return new(PerlNumeric.OfUnsigned((ulong)value), null, null);
    return new(PerlNumeric.Of((double)value), null, null);
  }

}
