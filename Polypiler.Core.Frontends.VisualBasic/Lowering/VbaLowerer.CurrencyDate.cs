using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VisualBasic.Lowering;

/// <summary>
/// <c>Currency</c> and <c>Date</c> ([MS-VBAL] §2.1, §5.6.9.3): a <c>Currency</c> is the IR's
/// <c>i64</c> of ten thousandths, a <c>Date</c> its <c>f64</c> of days since 1899-12-30 with the time
/// as the fraction. Their arithmetic and conversions are the helpers of <see cref="CurrencyDateHelperSource"/>.
/// </summary>
internal sealed partial class VbaLowerer {


  /// <summary>
  /// The type of <c>+</c>, <c>-</c> and <c>*</c> of two types ([MS-VBAL] §5.6.9.3 and the sections of
  /// the operators): a <c>Date</c> with anything a <c>Date</c>, but a product and the difference of two
  /// <c>Date</c>s a <c>Double</c>; a <c>Currency</c> with anything a <c>Currency</c>, but its product
  /// with a floating-point number or a <c>String</c> a <c>Double</c>; otherwise the wider of the two.
  /// </summary>
  private static VbaType ArithmeticType(string op, VbaType left, VbaType right) {
    if (left.Kind == VbaKind.Date || right.Kind == VbaKind.Date)
      return op == "*" || (op == "-" && left.Kind == VbaKind.Date && right.Kind == VbaKind.Date) ? VbaType.Double : VbaType.Date;
    if (left.Kind == VbaKind.Currency || right.Kind == VbaKind.Currency)
      return op == "*" && (left.IsFloat || right.IsFloat || left.Kind == VbaKind.String || right.Kind == VbaKind.String) ? VbaType.Double : VbaType.Currency;
    return Promote(left, right);
  }

  /// <summary>A number as a <c>Currency</c>: an integer exactly, a floating-point number, a <c>Date</c> or a <c>String</c> times 10000 rounded half to even, error 6 outside the range.</summary>
  private IrExpr ToCurrency(VbaValue value) {
    switch (value.Type.Kind) {
      case VbaKind.Currency:
        return value.Expr;
      case VbaKind.Boolean:
        return new IrConditional(value.Expr, new IrConst(IrType.I64, -10000), new IrConst(IrType.I64, 0));
      case VbaKind.Byte or VbaKind.Integer or VbaKind.Long or VbaKind.LongLong:
        return new IrBinary(IrBinaryOp.MultiplyChecked, this.Widen(value, IrType.I64), new IrConst(IrType.I64, 10000));
      default:
        return this.HelperCall("PlyCurFromD", this.ToFloat(this.AsNumber(value), IrType.F64));
    }
  }

  /// <summary>A number as a <c>Date</c>: its <c>Double</c>, error 6 outside the days a <c>Date</c> holds.</summary>
  private IrExpr ToDate(VbaValue value) => value.Type.Kind == VbaKind.Date ? value.Expr : this.HelperCall("PlyDateCheck", this.ToFloat(this.AsNumber(value), IrType.F64));

  /// <summary><c>+</c>, <c>-</c> or <c>*</c> in <c>Currency</c> or <c>Date</c>, or <see langword="null"/> for any other type.</summary>
  private VbaValue? ScaledArithmetic(string op, VbaType type, VbaValue left, VbaValue right) {
    if (type.Kind == VbaKind.Currency) {
      var (a, b) = (this.ToCurrency(left), this.ToCurrency(right));
      IrExpr result = op switch {
        "+" => new IrBinary(IrBinaryOp.AddChecked, a, b),
        "-" => new IrBinary(IrBinaryOp.SubtractChecked, a, b),
        _ => this.HelperCall("PlyCurMul", a, b)
      };
      return new(result, VbaType.Currency);
    }

    if (type.Kind != VbaKind.Date)
      return null;

    var sum = new IrBinary(op == "+" ? IrBinaryOp.Add : IrBinaryOp.Subtract, this.ToFloat(left, IrType.F64), this.ToFloat(right, IrType.F64));
    return new(this.HelperCall("PlyDateCheck", this.Checked(sum)), VbaType.Date);
  }

}
