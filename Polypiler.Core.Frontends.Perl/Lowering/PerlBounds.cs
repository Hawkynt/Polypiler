using System;
using System.Collections.Generic;
using System.Numerics;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl.Lowering;

/// <summary>Tracks exact integer intervals so arithmetic outside <c>use integer</c> stays within 64-bit integer kinds.</summary>
internal sealed class PerlBounds {

  private readonly Dictionary<IrExpr, Bounds> _proven = new(ReferenceEqualityComparer.Instance);

  public IrExpr ExactArithmetic(IrBinaryOp op, IrExpr left, IrExpr right, Func<string, Exception> fail) {
    var a = this.BoundsOf(left);
    var b = this.BoundsOf(right);
    var result = op switch {
      IrBinaryOp.Add => new Bounds(a.Min + b.Min, a.Max + b.Max),
      IrBinaryOp.Subtract => new Bounds(a.Min - b.Max, a.Max - b.Min),
      _ => Product(a, b)
    };
    var type = FixedType(result) ?? throw fail($"{op} that may promote Perl's integer result to float");
    var expression = new IrBinary(op, Promote(left, type), Promote(right, type));
    this._proven[expression] = result;
    return expression;
  }

  public IrType CommonType(IrExpr left, IrExpr right, Func<string, Exception> fail) {
    if (left.Type == right.Type)
      return left.Type;
    if (!left.Type.IsNumeric || !right.Type.IsNumeric)
      throw fail($"a result varying between {left.Type} and {right.Type}");
    if (left.Type.IsFloat || right.Type.IsFloat)
      return IrType.F64;

    var a = this.BoundsOf(left);
    var b = this.BoundsOf(right);
    return FixedType(new(BigInteger.Min(a.Min, b.Min), BigInteger.Max(a.Max, b.Max)))
      ?? throw fail("a result wider than 64-bit integers");
  }

  public bool FitsIn(IrExpr value, IrType target)
    => value.Type.IsInteger && target.IsInteger && BoundsOf(target).Covers(this.BoundsOf(value));

  public static IrType? FixedType(Bounds range)
    => BoundsOf(IrType.I64).Covers(range) ? IrType.I64
      : BoundsOf(IrType.U64).Covers(range) ? IrType.U64 : null;

  public Bounds BoundsOf(IrExpr value) {
    if (value is IrConst constant) {
      var number = BigInteger.CreateChecked(IrOperators.Number(constant.Type, constant.Value));
      return new(number, number);
    }
    return this._proven.TryGetValue(value, out var bounds) ? bounds : BoundsOf(value.Type);
  }

  public static Bounds BoundsOf(IrType type)
    => new(BigInteger.CreateChecked(IrOperators.Minimum(type)), BigInteger.CreateChecked(IrOperators.Maximum(type)));

  public static IrExpr Promote(IrExpr value, IrType type)
    => value.Type == type ? value : new IrConvert(type, value);

  private static Bounds Product(Bounds a, Bounds b) {
    var ll = a.Min * b.Min;
    var lh = a.Min * b.Max;
    var hl = a.Max * b.Min;
    var hh = a.Max * b.Max;
    return new(BigInteger.Min(BigInteger.Min(ll, lh), BigInteger.Min(hl, hh)),
      BigInteger.Max(BigInteger.Max(ll, lh), BigInteger.Max(hl, hh)));
  }

  internal readonly record struct Bounds(BigInteger Min, BigInteger Max) {
    public bool Covers(Bounds other) => this.Min <= other.Min && other.Max <= this.Max;
  }

}
