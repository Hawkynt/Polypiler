using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0121: a signed wrapping magnitude, from a sign-mask idiom or a comparison with zero.
/// Narrow values use the tree IR's promoted arithmetic and explicit wrapping conversions.
/// Every replacement requires a host proof of the actual expression, including those conversions.
/// Checked arithmetic and logical sign masks are refused; MIN maps to itself.
/// </summary>
public sealed class O0121AbsWrap : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0121AbsWrap() : base("O0121", "wrapping absolute value from a sign mask or a selection") { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    if (!expression.Type.IsInteger || !expression.Type.IsSigned)
      return null;

    var core = expression is IrConvert { Contract: IrConversion.Wrap, Operand.Type.IsInteger: true } conversion
      && conversion.Type.Bits < conversion.Operand.Type.Bits ? conversion.Operand : expression;
    var candidate = Find(core, context, out var reason);
    if (candidate is not { } found) {
      if (reason is not null)
        context.Note?.Invoke($"O0121 not applied: {reason}");
      return null;
    }

    var value = Images.Unwiden(found.Value);
    if (!value.Type.IsSigned || value.Type.Bits > expression.Type.Bits)
      return null;
    if (!IrShape.IsTrapFree(expression)) {
      context.Note?.Invoke("O0121 not applied: operand may trap or have effects");
      return null;
    }

    var after = Images.Call(IrIntrinsicOp.AbsWrap, expression.Type, Images.Fit(value, expression.Type));
    return context.Confirm(this.Id, expression, after, $"Abs.{expression.Type}[wrap] from {found.Form}");
  }

  private static (IrExpr Value, string Form)? Find(IrExpr expression, RuleContext context, out string? reason) {
    reason = null;
    if (expression is IrConditional choice)
      return Selected(choice, out reason) is { } selected ? (selected, "compare-select") : null;
    if (expression is not IrBinary { Op: IrBinaryOp.Subtract or IrBinaryOp.SubtractChecked, Left: IrBinary { Op: IrBinaryOp.Xor } }
      and not IrBinary { Op: IrBinaryOp.Xor, Left: IrBinary { Op: IrBinaryOp.Add } }
      and not IrBinary { Op: IrBinaryOp.Xor, Right: IrBinary { Op: IrBinaryOp.Add } })
      return null;

    var pairs = new List<(IrExpr Value, IrExpr First, IrExpr Second)>();
    var form = "xor-subtract";
    if (expression is IrBinary { Op: IrBinaryOp.Subtract or IrBinaryOp.SubtractChecked, Left: IrBinary { Op: IrBinaryOp.Xor } xor } subtract) {
      if (subtract.Op == IrBinaryOp.SubtractChecked) {
        reason = "checked arithmetic";
        return null;
      }

      pairs.Add((xor.Left, xor.Right, subtract.Right));
      pairs.Add((xor.Right, xor.Left, subtract.Right));
    } else if (expression is IrBinary { Op: IrBinaryOp.Xor } combined) {
      form = "add-xor";
      foreach (var (sum, mask) in new[] { (combined.Left, combined.Right), (combined.Right, combined.Left) })
        if (sum is IrBinary { Op: IrBinaryOp.Add } add) {
          pairs.Add((add.Left, add.Right, mask));
          pairs.Add((add.Right, add.Left, mask));
        }
    }

    foreach (var (value, first, second) in pairs) {
      if (Masked(value, first, second, context, out var refusal))
        return (value, form);
      if (reason is null || reason == "mask operand differs")
        reason = refusal;
    }

    return null;
  }

  private static IrExpr? Selected(IrConditional choice, out string? reason) {
    reason = null;
    if (choice.Condition is not IrBinary { Op: IrBinaryOp.Less or IrBinaryOp.LessOrEqual or IrBinaryOp.Greater or IrBinaryOp.GreaterOrEqual } comparison)
      return null;
    if (!comparison.Left.Type.IsSigned) {
      reason = "unsigned comparison";
      return null;
    }

    var (value, op) = Images.IsConstant(comparison.Right, out var right) && right == 0 ? (comparison.Left, comparison.Op)
      : Images.IsConstant(comparison.Left, out var left) && left == 0 ? (comparison.Right, Images.Mirror(comparison.Op))
      : (null, comparison.Op);
    if (value is null)
      return null;

    var negative = op is IrBinaryOp.Less or IrBinaryOp.LessOrEqual;
    var (negated, kept) = negative ? (choice.WhenTrue, choice.WhenFalse) : (choice.WhenFalse, choice.WhenTrue);
    if (negated is IrUnary { Op: IrUnaryOp.NegateChecked } or IrBinary { Op: IrBinaryOp.SubtractChecked }) {
      reason = "checked arithmetic";
      return null;
    }

    return Images.Negated(negated) is { } operand && IrShape.Same(operand, value) && IrShape.Same(kept, value) ? value : null;
  }

  private static bool Masked(IrExpr value, IrExpr first, IrExpr second, RuleContext context, out string reason) {
    reason = "mask operand differs";
    if (!IrShape.IsTrapFree(first) || !IrShape.IsTrapFree(second)
      || SignShift(first, context) is not { } shift || SignShift(second, context) is not { } other || !IrShape.Same(shift, other))
      return false;
    if (shift.Op == IrBinaryOp.ShiftRightLogical || shift.Op == IrBinaryOp.ShiftRight && !shift.Type.IsSigned) {
      reason = "logical right shift";
      return false;
    }
    if (shift.Op != IrBinaryOp.ShiftRight || !IrShape.Same(Images.Unwiden(shift.Left), Images.Unwiden(value)))
      return false;
    if (!Images.IsConstant(shift.Right, out var count) || count != shift.Type.Bits - 1 && count != Images.Unwiden(value).Type.Bits - 1) {
      reason = "shift amount is not width-1";
      return false;
    }

    return true;
  }

  /// <summary>Signed casts preserve a sign mask's zero or minus one; stable locals preserve its value.</summary>
  private static IrBinary? SignShift(IrExpr expression, RuleContext context) {
    var seen = new HashSet<IrExpr>(ReferenceEqualityComparer.Instance);
    while (seen.Add(expression)) {
      switch (expression) {
        case IrLocalRef reference when context.Definition(reference.Local) is { } definition:
          expression = definition;
          continue;
        case IrConvert { Contract: IrConversion.Wrap, Type.IsSigned: true, Operand.Type.IsInteger: true } conversion:
          expression = conversion.Operand;
          continue;
        default:
          return expression as IrBinary;
      }
    }

    return null;
  }
}
