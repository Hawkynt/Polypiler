using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0102: the SWAR population count - the mask-and-add steps of Hacker's Delight 5-1 on 16, 32 or 64
/// bits, with the first step as a subtraction or as two masked adds, and the final fold by a
/// multiplication or by shifts and adds - becomes <see cref="IrIntrinsicOp.PopCount"/>, in one
/// expression, through stable locals or as a run of assignments.
/// <para>
/// Recognition is by proof. A structural gate selects a candidate: the root is the final fold - a right
/// shift by at least 8, or a mask of at most 8 bits - and the expression holds the three SWAR mask
/// families <c>0x55…</c>, <c>0x33…</c> and <c>0x0F…</c>. The candidate must read exactly one variable,
/// have at most 24 operations, use masks of 16, 32 or 64 bits and neither trap nor act; each failure is
/// reported with its reason. The host's proof of the actual expression against <c>PopCount</c> of that
/// variable then decides, so a wrong mask, a missing step, a cut field or masks of another width are
/// refused as not equivalent to PopCount.
/// </para>
/// </summary>
public sealed class O0102PopCountSwar : ExpressionRule {

  /// <summary>The most operations a candidate may have.</summary>
  public const int MaximumSize = 24;

  /// <summary>Creates the rule.</summary>
  public O0102PopCountSwar() : base("O0102", "population count from the SWAR sequence", runs: true) { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    if (!IsFold(expression))
      return null;

    var (constants, variables, operations) = Walk(expression, context);
    var width = Width(constants, 0x55);
    if (width == 0 || Width(constants, 0x33) == 0 || Width(constants, 0x0F) == 0)
      return null;

    var reason = variables.Count != 1 ? "more than one variable"
      : !IrShape.IsTrapFree(expression) ? "operand may trap or have effects"
      : operations > MaximumSize ? "expression too large"
      : width is not (16 or 32 or 64) || !variables[0].Type.IsInteger ? "unsupported width"
      : null;
    if (reason is not null) {
      context.Note?.Invoke($"O0102 not applied: {reason}");
      return null;
    }

    // The counted value is the one variable, whatever arithmetic the frontend widened it to; the proof covers those conversions.
    var atom = variables[0];

    var after = Images.Fit(Images.Call(IrIntrinsicOp.PopCount, IrType.I32, atom), expression.Type);
    return context.Confirm(this.Id, expression, after, $"PopCount.{atom.Type} from SWAR sequence", "not equivalent to PopCount");
  }

  /// <summary>Whether an expression is a final fold: a right shift by at least 8 bits, or a mask of at most 8 bits.</summary>
  private static bool IsFold(IrExpr expression) => expression switch {
    IrBinary { Op: IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical, Type.IsInteger: true } shift
      => Images.IsConstant(shift.Right, out var count) && count >= 8,
    IrBinary { Op: IrBinaryOp.And, Type.IsInteger: true } and
      => Images.IsConstant(and.Right, out var mask) && mask is > 0 and <= 0xFF || Images.IsConstant(and.Left, out var other) && other is > 0 and <= 0xFF,
    _ => false
  };

  /// <summary>The integer constants, the distinct variables and the number of operations of an expression, through stable locals.</summary>
  private static (List<ulong> Constants, List<IrExpr> Variables, int Operations) Walk(IrExpr root, RuleContext context) {
    var constants = new List<ulong>();
    var variables = new List<IrExpr>();
    var seen = new HashSet<IrExpr>(ReferenceEqualityComparer.Instance);
    var operations = 0;
    var pending = new Stack<IrExpr>([root]);
    while (pending.Count > 0) {
      var node = pending.Pop();
      if (!seen.Add(node))
        continue;

      switch (node) {
        case IrConst { Type.IsInteger: true } constant:
          constants.Add((ulong)constant.Value & Weights.MaskOf(constant.Type.Bits));
          continue;
        case IrLocalRef reference when context.Definition(reference.Local) is { } definition:
          pending.Push(definition);
          continue;
        case IrLocalRef reference:
          if (!variables.Any(variable => IrShape.Same(variable, reference)))
            variables.Add(reference);
          continue;
      }

      var operands = IrShape.Operands(node);
      if (operands.Count == 0) {
        if (!variables.Any(variable => IrShape.Same(variable, node)))
          variables.Add(node);
        continue;
      }

      ++operations;
      foreach (var operand in operands)
        pending.Push(operand);
    }

    return (constants, variables, operations);
  }

  /// <summary>The widest of 8, 16, 32 and 64 bits at which a constant repeats a byte, or 0 when none does.</summary>
  private static int Width(List<ulong> constants, byte pattern) {
    foreach (var width in new[] { 64, 32, 16, 8 }) {
      var repeated = 0UL;
      for (var shift = 0; shift < width; shift += 8)
        repeated |= (ulong)pattern << shift;
      if (constants.Contains(repeated))
        return width;
    }

    return 0;
  }

}
