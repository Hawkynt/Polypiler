using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Batch;

public static partial class BatchFrontend {

  private sealed class Script(IReadOnlyList<Instruction> instructions, IReadOnlyDictionary<string, int> labels,
    IReadOnlyDictionary<string, IrLocal> locals, Action<int[][], int[]>? validate = null) {

    public IrModule Lift(string name) {
      var edges = Enumerable.Range(0, instructions.Count).Select(this.Successors).ToArray();
      var reachable = this.Reachable(edges);
      this.CheckAssignments(edges, reachable);
      this.CheckReducible(edges, reachable);
      validate?.Invoke(edges, reachable);

      var pc = new IrLocal("batchPc", IrType.I32);
      var result = new IrLocal("batchResult", IrType.I32);
      var used = reachable.SelectMany(index => References(instructions[index])).ToHashSet();
      var iterators = new List<IrLocal>();
      foreach (var index in reachable)
        switch (instructions[index]) {
          case Assignment { Value: not (IrConst or IrStrConst) } assignment:
            used.Add(assignment.Destination);
            break;
          case NumericLoop { HasIterations: true } range:
            used.Add(range.Body.Destination);
            iterators.Add(range.Iterator);
            break;
        }
      var storage = locals.Values.Where(used.Contains).ToArray();
      var sections = reachable.Select(index => this.Section(index, edges[index], pc, result, used)).ToArray();
      var drive = new IrWhile(new IrBinary(IrBinaryOp.NotEqual, new IrLocalRef(pc), Number(-1)),
        new([new IrSwitch(new IrLocalRef(pc), sections)]));

      // Definite assignment proves these storage initializers are never observable.
      IrStmt[] statements = [.. storage.Select(local => new IrDeclare(local, local.Type.IsString ? new IrStrConst("") : Number(0))),
        new IrDeclare(pc, Number(0)), new IrDeclare(result, Number(0)), drive, new IrReturn(new IrLocalRef(result))];
      IrFunction run = new("Run", true, IrType.I32, [], [.. storage, .. iterators, pc, result], new(statements));
      return new(name, [run]);
    }

    private int[] Successors(int index) => instructions[index] switch {
      Exit => [],
      Jump { Condition: null } jump => [this.Target(jump)],
      Jump jump => [this.Target(jump), index + 1],
      _ => [index + 1]
    };

    private int Target(Jump jump) => labels.TryGetValue(jump.Target, out var index) ? index
      : throw new Refusal(jump.Line, $"unknown label {jump.Target}", malformed: true);

    private int[] Reachable(int[][] edges) {
      var visited = new HashSet<int> { 0 };
      var pending = new Queue<int>([0]);
      while (pending.TryDequeue(out var index))
        foreach (var target in edges[index]) {
          if (target == instructions.Count)
            throw new Refusal(instructions[index].Line, "reachable control flow falls off the end", malformed: true);
          if (visited.Add(target))
            pending.Enqueue(target);
        }
      return [.. visited.Order()];
    }

    private void CheckAssignments(int[][] edges, int[] reachable) {
      var entry = new Dictionary<int, HashSet<IrLocal>> { [0] = [] };
      var pending = new Queue<int>([0]);
      while (pending.TryDequeue(out var index)) {
        var written = new HashSet<IrLocal>(entry[index]);
        if (instructions[index] is Assignment assignment)
          written.Add(assignment.Destination);
        else if (instructions[index] is NumericLoop { HasIterations: true } range)
          written.Add(range.Body.Destination);

        foreach (var target in edges[index]) {
          if (!entry.TryGetValue(target, out var existing)) {
            entry.Add(target, new(written));
            pending.Enqueue(target);
          } else if (!existing.IsSubsetOf(written)) {
            existing.IntersectWith(written);
            pending.Enqueue(target);
          }
        }
      }

      var names = locals.ToDictionary(pair => pair.Value, pair => pair.Key);
      foreach (var index in reachable)
        foreach (var local in References(instructions[index]))
          if (!entry[index].Contains(local))
            throw new Refusal(instructions[index].Line, $"read of {names[local]} before assignment");
    }

    private static IEnumerable<IrLocal> References(Instruction instruction) {
      if (instruction is NumericLoop range)
        return range.HasIterations ? References(range.Body).Where(local => local != range.Iterator) : [];

      var expression = instruction switch {
        Assignment assignment => assignment.Value,
        Jump jump => jump.Condition,
        Exit exit => exit.Status,
        _ => null
      };
      return expression is null ? [] : IrTree.Nodes(expression).OfType<IrLocalRef>().Select(reference => reference.Local);
    }

    private void CheckReducible(int[][] edges, int[] reachable) {
      var previous = reachable.ToDictionary(index => index, _ => new List<int>());
      foreach (var from in reachable)
        foreach (var to in edges[from])
          previous[to].Add(from);

      var (starts, ends) = DominatorIntervals(edges, previous);
      var forward = reachable.ToDictionary(from => from,
        from => edges[from].Where(to => starts[from] < starts[to] || starts[from] >= ends[to]).ToArray());
      var indegree = reachable.ToDictionary(index => index, _ => 0);
      foreach (var from in reachable)
        foreach (var to in forward[from])
          ++indegree[to];

      var pending = new Queue<int>(reachable.Where(index => indegree[index] == 0));
      while (pending.TryDequeue(out var from))
        foreach (var to in forward[from])
          if (--indegree[to] == 0)
            pending.Enqueue(to);

      foreach (var index in reachable)
        if (indegree[index] != 0)
          throw new Refusal(instructions[index].Line, "irreducible control flow");
    }

    private static (int[] Starts, int[] Ends) DominatorIntervals(int[][] edges, Dictionary<int, List<int>> predecessors) {
      // Immediate dominators in reverse postorder (Cooper, Harvey and Kennedy, Rice TR-06-33870).
      var order = new List<int>();
      WalkDepthFirst(edges, order.Add);
      order.Reverse();
      var ranks = new int[edges.Length];
      for (var rank = 0; rank < order.Count; ++rank)
        ranks[order[rank]] = rank;

      var immediate = new int[edges.Length];
      Array.Fill(immediate, -1);
      immediate[0] = 0;
      bool changed;
      do {
        changed = false;
        foreach (var block in order.Skip(1)) {
          var parent = -1;
          foreach (var predecessor in predecessors[block]) {
            if (immediate[predecessor] < 0)
              continue;
            parent = parent < 0 ? predecessor : IntersectDominators(parent, predecessor, immediate, ranks);
          }
          if (immediate[block] == parent)
            continue;
          immediate[block] = parent;
          changed = true;
        }
      } while (changed);

      var tree = Enumerable.Range(0, edges.Length).Select(_ => new List<int>()).ToArray();
      foreach (var block in order.Skip(1))
        tree[immediate[block]].Add(block);

      // A node dominates exactly the nodes in its subtree's preorder interval.
      var starts = new int[edges.Length];
      var ends = new int[edges.Length];
      var next = 0;
      WalkDepthFirst(tree, block => ends[block] = next, block => starts[block] = next++);
      return (starts, ends);
    }

    private static int IntersectDominators(int left, int right, int[] immediate, int[] ranks) {
      while (left != right)
        if (ranks[left] > ranks[right])
          left = immediate[left];
        else
          right = immediate[right];
      return left;
    }

    private static void WalkDepthFirst(IReadOnlyList<int>[] graph, Action<int> leave, Action<int>? enter = null) {
      var visited = new bool[graph.Length];
      var pending = new Stack<(int Node, bool Exiting)>([(0, false)]);
      while (pending.TryPop(out var current)) {
        if (current.Exiting) {
          leave(current.Node);
          continue;
        }
        if (visited[current.Node])
          continue;
        visited[current.Node] = true;
        enter?.Invoke(current.Node);
        pending.Push((current.Node, true));
        foreach (var successor in graph[current.Node])
          pending.Push((successor, false));
      }
    }

    private IrSwitchSection Section(int index, int[] edges, IrLocal pc, IrLocal result, IReadOnlySet<IrLocal> used) {
      IrAssign Go(int destination) => new(pc, Number(destination));
      IrStmt[] statements = instructions[index] switch {
        Assignment assignment when used.Contains(assignment.Destination) => [new IrAssign(assignment.Destination, assignment.Value), Go(edges[0])],
        Assignment => [Go(edges[0])],
        NumericLoop { HasIterations: true } range => [ForRange(range), Go(edges[0])],
        NumericLoop => [Go(edges[0])],
        Jump { Condition: { } condition } => [new IrIf(condition, new([Go(edges[0])]), new([Go(edges[1])]))],
        Jump => [Go(edges[0])],
        Exit exit => [new IrAssign(result, exit.Status), Go(-1)],
        _ => throw new InvalidOperationException("unexpected Batch instruction")
      };
      return new([index], false, new(statements));
    }

    private static IrBlock ForRange(NumericLoop range) {
      var iterator = new IrLocalRef(range.Iterator);
      var comparison = range.Step.Value > 0 ? IrBinaryOp.LessOrEqual : IrBinaryOp.GreaterOrEqual;
      var condition = new IrBinary(comparison, iterator, range.End);
      var body = new IrBlock([new IrAssign(range.Body.Destination, range.Body.Value)]);
      var step = new IrBlock([new IrAssign(range.Iterator, new IrBinary(IrBinaryOp.Add, iterator, range.Step))]);
      return new([new IrDeclare(range.Iterator, range.Start), new IrFor(condition, body, step)]);
    }

    private static IrConst Number(int value) => new(IrType.I32, value);

  }

}
