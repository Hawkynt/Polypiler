using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize;

/// <summary>Bounded cascade candidates and diagnostic classification for the permutation rules.</summary>
internal static class PermutationGate {

  public static IReadOnlyList<IrExpr> Nodes(IrExpr expression, RuleContext context) {
    var nodes = new List<IrExpr>();
    var seen = new HashSet<IrExpr>(ReferenceEqualityComparer.Instance);
    var pending = new Stack<IrExpr>([expression]);
    while (pending.TryPop(out var node)) {
      if (!seen.Add(node))
        continue;

      nodes.Add(node);
      if (node is IrLocalRef reference && context.Definition(reference.Local) is { } definition)
        pending.Push(definition);
      else
        foreach (var operand in IrShape.Operands(node))
          pending.Push(operand);
    }

    return nodes;
  }

  public static bool HasSwapMask(IReadOnlyList<IrExpr> nodes, int width)
    => nodes.OfType<IrConst>().Any(constant => (ulong)constant.Value == (ulong.MaxValue / 3 >> (64 - width)));

  public static bool WithinBudget(IReadOnlyList<IrExpr> nodes, int width, RuleContext context) {
    if (!HasSwapMask(nodes, width) || nodes.Count(node => node is IrBinary or IrUnary or IrConvert or IrIntrinsic) <= 8 * BitOperations.Log2((uint)width))
      return true;

    context.Note?.Invoke("O0105 not applied: expression too large");
    return false;
  }

  public static void Refused(IrExpr expression, RuleContext context, bool bytes) {
    var nodes = Nodes(expression, context);
    var shifts = nodes.OfType<IrBinary>().Where(binary => binary.Op is IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical).ToList();
    if (bytes ? shifts.Count(shift => shift.Right is IrConst constant && constant.Value != 0 && constant.Value % 8 == 0) < 2
      : !HasSwapMask(nodes, expression.Type.Bits))
      return;

    // A plain non-half rotation is not a byte-swap candidate. The half rotation is the
    // registered word-swap counterexample; assembled byte terms have more than two shifts.
    if (bytes && shifts.Count == 2 && !shifts.All(shift => shift.Right is IrConst constant && constant.Value == expression.Type.Bits / 2))
      return;

    var reason = bytes
      ? shifts.Any(shift => shift.Op == IrBinaryOp.ShiftRight && shift.Type.IsSigned) ? "sign bits reach the result" : "byte provenance is not a reversal"
      : "not equivalent to ReverseBits";
    context.Note?.Invoke($"{(bytes ? "O0106" : "O0105")} not applied: {reason}");
  }

  public static void LoopRefused(IReadOnlyList<IrStmt> statements, int index, RuleContext context, string id, int unit) {
    if (index + 1 >= statements.Count || statements[index + 1] is not IrWhile loop)
      return;

    var nodes = IrStatements.AllExpressions(loop.Body).SelectMany(IrShape.Nodes).OfType<IrBinary>().ToList();
    if (!nodes.Any(node => node.Op == IrBinaryOp.ShiftLeft && node.Right is IrConst count && count.Value == unit)
      || !nodes.Any(node => node.Op == IrBinaryOp.And && node.Right is IrConst mask && mask.Value == (1 << unit) - 1))
      return;

    string? reason = null;
    if (loop.Body.Statements.Any(statement => CounterLoop.Assignment(statement) is null))
      reason = "loop body has effects";
    else if (CounterLoop.At(statements, index, context) is { } counted && counted.Passes() is { } passes
      && nodes.First(node => node.Op == IrBinaryOp.ShiftLeft).Type.Bits / unit != passes)
      reason = unit == 8 ? "trip count differs from byte count" : "trip count differs from width";

    if (reason is not null)
      context.Note?.Invoke($"{id} not applied: {reason}");
  }
}
