using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Batch;

public static partial class BatchFrontend {

  private static void CheckDecimalTextOperands(IReadOnlyList<Instruction> instructions, int[][] edges, int[] reachable,
    IrSwitchSection[] sections, IrLocal[] storage, IrLocal pc) {
    var checks = reachable.Where(index => instructions[index] is Assignment { DecimalTextOperands: not null }).ToArray();
    if (checks.Length == 0)
      return;

    // A literal and any copy of a representable value are representable. This also discharges
    // unchanged operands in large loops and scope saves without traversing each iteration.
    var possiblyMinimum = PossiblyMinimumLocals(sections, pc);
    checks = checks.Where(index => ((Assignment)instructions[index]).DecimalTextOperands!.Any(possiblyMinimum.Contains)).ToArray();
    if (checks.Length == 0)
      return;

    var needed = PredecessorsOfChecks(edges, reachable, checks);
    var bodies = sections.ToDictionary(section => (int)section.Labels[0], section => section.Body);
    var initial = storage.ToDictionary(local => local, _ => 0);
    var pending = new Stack<(int Index, Dictionary<IrLocal, int> Values)>([(0, initial)]);
    var remaining = _maxExpandedInstructions;

    // Propagate constants only into the graph that can reach an unresolved text read. Keep
    // paths separate so a known loop counter or branch can discharge a read at its actual phase.
    while (pending.TryPop(out var path)) {
      var (index, values) = path;
      while (needed[index]) {
        if (remaining-- == 0)
          throw new Refusal(instructions[checks[0]].Line, "cannot prove SET /A decimal expansion within the analysis budget");

        var instruction = instructions[index];
        if (instruction is Assignment { DecimalTextOperands: { } operands })
          foreach (var operand in operands)
            if (!values.TryGetValue(operand, out var value) || value == int.MinValue)
              throw new Refusal(instruction.Line, "expanded operand can exceed the SET /A decimal magnitude limit");

        foreach (var statement in bodies[index].Statements)
          if (statement is IrAssign assignment && assignment.Local != pc) {
            if (ConstantValue(assignment.Value, values) is { } value)
              values[assignment.Local] = value;
            else
              values.Remove(assignment.Local);
          }

        var successors = edges[index];
        if (instruction is Jump { Condition: { } condition }) {
          var outcome = ConstantValue(condition, values);
          if (outcome is null && needed[successors[1]])
            pending.Push((successors[1], new(values)));
          index = outcome == 0 ? successors[1] : successors[0];
        } else {
          index = successors[0];
        }
      }
    }
  }

  private static HashSet<IrLocal> PossiblyMinimumLocals(IrSwitchSection[] sections, IrLocal pc) {
    var unsafeLocals = new HashSet<IrLocal>();
    var copies = new Dictionary<IrLocal, List<IrLocal>>();
    foreach (var assignment in sections.SelectMany(section => section.Body.Statements).OfType<IrAssign>()) {
      if (assignment.Local == pc)
        continue;
      if (assignment.Value is IrLocalRef reference) {
        if (!copies.TryGetValue(reference.Local, out var destinations)) {
          destinations = [];
          copies.Add(reference.Local, destinations);
        }
        destinations.Add(assignment.Local);
      } else if (ConstantValue(assignment.Value, _emptyValues) is null or int.MinValue) {
        unsafeLocals.Add(assignment.Local);
      }
    }

    var pending = new Queue<IrLocal>(unsafeLocals);
    while (pending.TryDequeue(out var source))
      if (copies.TryGetValue(source, out var destinations))
        foreach (var destination in destinations)
          if (unsafeLocals.Add(destination))
            pending.Enqueue(destination);
    return unsafeLocals;
  }

  private static bool[] PredecessorsOfChecks(int[][] edges, int[] reachable, int[] checks) {
    var predecessors = new List<int>[edges.Length];
    foreach (var from in reachable)
      foreach (var to in edges[from])
        (predecessors[to] ??= []).Add(from);

    var needed = new bool[edges.Length];
    var pending = new Queue<int>(checks);
    foreach (var check in checks)
      needed[check] = true;
    while (pending.TryDequeue(out var index)) {
      if (predecessors[index] is not { } inputs)
        continue;
      foreach (var input in inputs)
        if (!needed[input]) {
          needed[input] = true;
          pending.Enqueue(input);
        }
    }
    return needed;
  }

  private static readonly IReadOnlyDictionary<IrLocal, int> _emptyValues = new Dictionary<IrLocal, int>();

  private static int? ConstantValue(IrExpr expression, IReadOnlyDictionary<IrLocal, int> values) {
    switch (expression) {
      case IrConst constant:
        return (int)constant.Value;
      case IrLocalRef reference:
        return values.TryGetValue(reference.Local, out var value) ? value : null;
      case IrUnary { Op: IrUnaryOp.Negate } unary:
        return unchecked(-ConstantValue(unary.Operand, values));
      case IrBinary binary when ConstantValue(binary.Left, values) is { } left
          && ConstantValue(binary.Right, values) is { } right:
        return binary.Op switch {
          IrBinaryOp.Add => unchecked(left + right),
          IrBinaryOp.Subtract => unchecked(left - right),
          IrBinaryOp.Multiply => unchecked(left * right),
          IrBinaryOp.And => left & right,
          IrBinaryOp.ShiftRight => left >> right,
          IrBinaryOp.Equal => left == right ? 1 : 0,
          IrBinaryOp.NotEqual => left != right ? 1 : 0,
          IrBinaryOp.Less => left < right ? 1 : 0,
          IrBinaryOp.LessOrEqual => left <= right ? 1 : 0,
          IrBinaryOp.Greater => left > right ? 1 : 0,
          IrBinaryOp.GreaterOrEqual => left >= right ? 1 : 0,
          _ => null
        };
      default:
        return null;
    }
  }

}
