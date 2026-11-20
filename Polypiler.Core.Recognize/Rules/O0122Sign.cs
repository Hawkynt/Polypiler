using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0122: the three-way sign of an integer becomes <see cref="IrIntrinsicOp.Sign"/>.
/// <para>
/// Two families are matched. A <em>sign-class</em> expression is built only of integer constants,
/// comparisons of one value with zero, selections and wrapping sums or differences of them; its
/// result is a function of whether the value is negative, zero or positive, so evaluating it for the
/// three classes decides it: <c>(x &gt; 0 ? 1 : 0) - (x &lt; 0 ? 1 : 0)</c> (how C# writes
/// <c>(x &gt; 0) - (x &lt; 0)</c>), and nested selections such as
/// <c>x &gt; 0 ? 1 : x &lt; 0 ? -1 : 0</c> in every order and spelling. The <em>shift-or</em> form is
/// <c>(x &gt;&gt; (w - 1)) | (-x &gt;&gt;&gt; (w - 1))</c>; whether the operand of the logical shift is
/// the negation is decided on its shape (<c>-x</c>, <c>0 - x</c>) or on its weighted bits (<see cref="WeightEvaluator"/>).
/// </para>
/// <para>
/// Narrow values are matched on the tree IR's promoted arithmetic. Every replacement requires a host
/// proof of the actual expression, including its conversions. A sign-class expression that separates
/// the three classes but gives other values - the swapped difference - and a shift-or whose second
/// operand is no logical shift of the negation - <c>(x &gt;&gt; 31) | 1</c> - are refused as not
/// equivalent to Sign; an unsigned comparison and comparisons of two different values are refused
/// with their own reasons.
/// </para>
/// </summary>
public sealed class O0122Sign : ExpressionRule {

  private const string NotSign = "not equivalent to Sign";

  /// <summary>Creates the rule.</summary>
  public O0122Sign() : base("O0122", "sign from comparisons or shifts") { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    // An enum is no integer to convert the sign to, even when its members are -1, 0 and 1.
    if (!expression.Type.IsInteger || expression.Type.IsEnum)
      return null;

    var core = expression is IrConvert { Contract: IrConversion.Wrap, Operand.Type.IsInteger: true } conversion
      && conversion.Type.Bits < conversion.Operand.Type.Bits ? conversion.Operand : expression;
    var (value, form, reason) = core switch {
      IrBinary { Op: IrBinaryOp.Or } or => Shifted(or, context),
      IrConditional or IrBinary { Op: IrBinaryOp.Subtract or IrBinaryOp.Add } => Classified(core),
      _ => (null, "", null)
    };
    if (value is null) {
      if (reason is not null)
        context.Note?.Invoke($"O0122 not applied: {reason}");
      return null;
    }

    if (!IrShape.IsTrapFree(expression)) {
      context.Note?.Invoke("O0122 not applied: operand may trap or have effects");
      return null;
    }

    var after = Images.Fit(Images.Call(IrIntrinsicOp.Sign, IrType.I32, value), expression.Type);
    return context.Confirm(this.Id, expression, after, $"Sign.{Images.Unwiden(value).Type} from {form}");
  }

  /// <summary>The value of a sign-class expression that is Sign, its form, or the reason it is refused.</summary>
  private static (IrExpr? Value, string Form, string? Reason) Classified(IrExpr expression) {
    var classes = new SignClasses();
    if (classes.Evaluate(expression, -1) is not { } negative || classes.Evaluate(expression, 0) is not { } zero
      || classes.Evaluate(expression, 1) is not { } positive)
      return (null, "", null);

    // Only an expression that separates all three classes was meant as a sign; a partial selection is a step towards one.
    if (negative == zero || zero == positive || negative == positive)
      return (null, "", null);
    if (classes.Refusal is { } refusal)
      return (null, "", refusal);
    if (negative != IrOperators.Wrap(expression.Type, -1) || zero != 0 || positive != 1)
      return (null, "", NotSign);

    // Results that differ by class were selected by a comparison, which set the value.
    return (classes.Value, expression is IrConditional ? "select" : "compare-difference", null);
  }

  /// <summary>
  /// Evaluates a sign-class expression, as the bits of its type, for one class of its compared value.
  /// An unsigned comparison or a second compared value is recorded as a refusal and evaluated as if
  /// it were neither, so that only an expression meant as a sign reports it.
  /// </summary>
  private sealed class SignClasses {

    /// <summary>The value the first comparison reads.</summary>
    public IrExpr? Value { get; private set; }

    /// <summary>Why the expression is refused although it has the shape of a sign.</summary>
    public string? Refusal { get; private set; }

    /// <summary>The result for a value of the sign <paramref name="sign"/>, or <see langword="null"/>.</summary>
    public long? Evaluate(IrExpr expression, int sign) {
      switch (expression) {
        case IrConst { Type.IsInteger: true } constant:
          return IrOperators.Wrap(constant.Type, constant.Value);
        case IrConditional choice:
          return this.Test(choice.Condition, sign) switch {
            true => this.Evaluate(choice.WhenTrue, sign),
            false => this.Evaluate(choice.WhenFalse, sign),
            null => null
          };
        case IrBinary { Op: IrBinaryOp.Subtract or IrBinaryOp.Add } sum:
          return this.Evaluate(sum.Left, sign) is { } left && this.Evaluate(sum.Right, sign) is { } right
            ? IrOperators.Wrap(sum.Type, unchecked(sum.Op == IrBinaryOp.Add ? left + right : left - right)) : null;
        case IrConvert { Contract: IrConversion.Wrap, Operand.Type.IsInteger: true, Type.IsInteger: true } conversion:
          return this.Evaluate(conversion.Operand, sign) is { } inner ? IrOperators.Wrap(conversion.Type, unchecked((long)IrOperators.Number(conversion.Operand.Type, inner))) : null;
        default:
          return null;
      }
    }

    /// <summary>The truth of a comparison with zero for a value of the sign <paramref name="sign"/>.</summary>
    private bool? Test(IrExpr condition, int sign) {
      if (condition is not IrBinary {
        Op: IrBinaryOp.Less or IrBinaryOp.LessOrEqual or IrBinaryOp.Greater or IrBinaryOp.GreaterOrEqual
        or IrBinaryOp.Equal or IrBinaryOp.NotEqual, Left.Type.IsInteger: true
      } comparison)
        return null;

      var (value, op) = Images.IsConstant(comparison.Right, out var right) && right == 0 ? (comparison.Left, comparison.Op)
        : Images.IsConstant(comparison.Left, out var left) && left == 0 ? (comparison.Right, Images.Mirror(comparison.Op))
        : (null, comparison.Op);
      if (value is null)
        return null;
      if (!value.Type.IsSigned && op is not (IrBinaryOp.Equal or IrBinaryOp.NotEqual))
        this.Refusal ??= "unsigned comparison";
      if (this.Value is null)
        this.Value = value;
      else if (!IrShape.Same(this.Value, value))
        this.Refusal ??= "operands differ";

      return op switch {
        IrBinaryOp.Less => sign < 0,
        IrBinaryOp.LessOrEqual => sign <= 0,
        IrBinaryOp.Greater => sign > 0,
        IrBinaryOp.GreaterOrEqual => sign >= 0,
        IrBinaryOp.Equal => sign == 0,
        _ => sign != 0
      };
    }
  }

  /// <summary><c>(x &gt;&gt; (w - 1)) | (-x &gt;&gt;&gt; (w - 1))</c> in either order: the <c>x</c>, or why it is refused.</summary>
  private static (IrExpr? Value, string Form, string? Reason) Shifted(IrBinary or, RuleContext context) {
    var candidate = false;
    foreach (var (arithmetic, logical) in new[] { (or.Left, or.Right), (or.Right, or.Left) }) {
      if (Images.Peel(arithmetic) is not IrBinary { Op: IrBinaryOp.ShiftRight, Type.IsSigned: true } sign || !Images.IsConstant(sign.Right, out var top))
        continue;

      // A sign mask or-ed with a constant or another right shift was meant as a sign; anything else is unrelated.
      candidate |= Images.IsConstant(logical, out _) || Images.Peel(logical) is IrBinary { Op: IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical };
      var value = sign.Left;
      var width = sign.Type.Bits;
      if (top != width - 1 && top != Images.Unwiden(value).Type.Bits - 1)
        continue;

      if (Images.Peel(logical) is IrBinary { Op: IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical } shift
        && (shift.Op == IrBinaryOp.ShiftRightLogical || !shift.Type.IsSigned)
        && shift.Type.Bits == width && Images.IsConstant(shift.Right, out var count) && count == width - 1
        && (Images.Negated(Images.Peel(shift.Left)) is { } negated && IrShape.Same(negated, value) || Images.Computes(shift.Left, value, bits => bits.Negate(), context)))
        return (value, "shift-or", null);
    }

    return (null, "", candidate ? NotSign : null);
  }

}
