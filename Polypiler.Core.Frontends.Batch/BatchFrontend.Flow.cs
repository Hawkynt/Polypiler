using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Batch;

public static partial class BatchFrontend {

  private sealed class Script(IReadOnlyList<Instruction> instructions, IReadOnlyDictionary<string, int> labels,
    IReadOnlyDictionary<string, IrLocal> locals, Action<int[][], int[]>? validate = null,
    IReadOnlySet<IrLocal>? preassigned = null, IReadOnlySet<IrLocal>? scopedLocals = null, bool initialDelayedExpansion = false) {

    public IrModule Lift(string name) {
      var edges = Enumerable.Range(0, instructions.Count).Select(this.Successors).ToArray();
      var reachable = this.Reachable(edges);
      var (entryDepths, entryAssigned) = this.CheckAssignments(edges, reachable);
      this.CheckReducible(edges, reachable);
      validate?.Invoke(edges, reachable);

      var pc = new IrLocal("batchPc", IrType.I32);
      var result = new IrLocal("batchResult", IrType.I32);
      var used = reachable.SelectMany(index => References(instructions[index])).ToHashSet();
      foreach (var index in reachable)
        if (instructions[index] is Assignment { Value: not (IrConst or IrStrConst) } assignment)
          used.Add(assignment.Destination);

      var backups = this.BuildScopeBackups(reachable, entryDepths, entryAssigned, used);
      var storage = locals.Values.Where(used.Contains).Concat(backups.Values).Distinct().ToArray();
      var sections = reachable.Select(index => this.Section(index, edges[index], pc, result, used, entryDepths, entryAssigned, backups)).ToArray();
      CheckDecimalTextOperands(instructions, edges, reachable, sections, storage, pc);
      var drive = new IrWhile(new IrBinary(IrBinaryOp.NotEqual, new IrLocalRef(pc), Number(-1)),
        new([new IrSwitch(new IrLocalRef(pc), sections)]));

      // Definite assignment proves these storage initializers are never observable (except preassigned state like errorlevel).
      IrStmt[] statements = [.. storage.Select(local => new IrDeclare(local, local.Type.IsString ? new IrStrConst("") : Number(0))),
        new IrDeclare(pc, Number(0)), new IrDeclare(result, Number(0)), drive, new IrReturn(new IrLocalRef(result))];
      IrFunction run = new("Run", true, IrType.I32, [], [.. storage, pc, result], new(statements));
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

    private sealed class ScopeFrame(int frameId, HashSet<IrLocal> assigned, bool? delayedExpansion) {
      public int FrameId { get; } = frameId;
      public HashSet<IrLocal> Assigned { get; } = assigned;
      public bool? DelayedExpansion { get; set; } = delayedExpansion;
      public ScopeFrame Clone() => new(this.FrameId, new(this.Assigned), this.DelayedExpansion);
    }

    private sealed class ScopeState(List<ScopeFrame> frames) {
      public List<ScopeFrame> Frames { get; } = frames;
      public int Depth => this.Frames.Count - 1;
      public ScopeFrame Current => this.Frames[^1];

      public ScopeState Clone() => new([.. this.Frames.Select(frame => frame.Clone())]);
    }

    private (Dictionary<int, int> Depths, Dictionary<int, List<(int FrameId, HashSet<IrLocal> Assigned)>> Assigned)
      CheckAssignments(int[][] edges, int[] reachable) {
      var scoped = scopedLocals ?? locals.Values.ToHashSet();
      var initial = preassigned is null ? new HashSet<IrLocal>() : new HashSet<IrLocal>(preassigned);
      var entry = new Dictionary<int, ScopeState> { [0] = new([new(-1, initial, initialDelayedExpansion)]) };
      var pending = new Queue<int>([0]);

      while (pending.TryDequeue(out var index)) {
        var state = entry[index].Clone();
        switch (instructions[index]) {
          case Assignment assignment:
            state.Current.Assigned.Add(assignment.Destination);
            if (!scoped.Contains(assignment.Destination))
              foreach (var frame in state.Frames)
                frame.Assigned.Add(assignment.Destination);
            break;
          case ScopePush push:
            state.Frames.Add(new(push.FrameId, new(state.Current.Assigned), push.DelayedExpansion ?? state.Current.DelayedExpansion));
            break;
          case ScopePop pop:
            if (state.Frames.Count <= 1 || state.Frames[^1].FrameId != pop.FrameId)
              throw new Refusal(pop.Line, "unmatched ENDLOCAL");
            state.Frames.RemoveAt(state.Frames.Count - 1);
            break;
          case FrameUnwind unwind:
            while (state.Frames.Count > 1 && state.Frames[^1].FrameId == unwind.FrameId)
              state.Frames.RemoveAt(state.Frames.Count - 1);
            break;
        }

        foreach (var target in edges[index]) {
          if (!entry.TryGetValue(target, out var existing)) {
            entry.Add(target, state.Clone());
            pending.Enqueue(target);
          } else {
            if (existing.Frames.Count != state.Frames.Count
                || !Enumerable.Range(0, existing.Frames.Count).All(level => existing.Frames[level].FrameId == state.Frames[level].FrameId))
              throw new Refusal(instructions[target].Line, "unbalanced SETLOCAL/ENDLOCAL scope");
            var changed = false;
            for (var level = 0; level < existing.Frames.Count; ++level) {
              var incoming = state.Frames[level];
              var merged = existing.Frames[level];
              if (!merged.Assigned.IsSubsetOf(incoming.Assigned)) {
                merged.Assigned.IntersectWith(incoming.Assigned);
                changed = true;
              }
              if (merged.DelayedExpansion is not null && merged.DelayedExpansion != incoming.DelayedExpansion) {
                merged.DelayedExpansion = null;
                changed = true;
              }
            }
            if (changed)
              pending.Enqueue(target);
          }
        }
      }

      var names = locals.ToDictionary(pair => pair.Value, pair => pair.Key);
      foreach (var index in reachable) {
        var current = entry[index].Current;
        var instruction = instructions[index];
        if (instruction is Assignment { ReadsDelayedExpansion: true } && current.DelayedExpansion is not true)
          throw new Refusal(instruction.Line, "delayed expansion must be enabled on every path to this read");
        foreach (var local in References(instruction))
          if (!current.Assigned.Contains(local))
            throw new Refusal(instruction.Line, $"read of {names.GetValueOrDefault(local, local.Name)} before assignment");
      }

      return (entry.ToDictionary(pair => pair.Key, pair => pair.Value.Depth),
        entry.ToDictionary(pair => pair.Key, pair => pair.Value.Frames.Select(frame => (frame.FrameId, frame.Assigned)).ToList()));
    }

    private Dictionary<(int Depth, IrLocal Local), IrLocal> BuildScopeBackups(
      int[] reachable, Dictionary<int, int> entryDepths,
      Dictionary<int, List<(int FrameId, HashSet<IrLocal> Assigned)>> entryAssigned, HashSet<IrLocal> used) {
      var scoped = scopedLocals ?? locals.Values.ToHashSet();
      var backups = new Dictionary<(int Depth, IrLocal Local), IrLocal>();
      foreach (var index in reachable) {
        if (instructions[index] is not ScopePush)
          continue;
        var depth = entryDepths[index] + 1;
        foreach (var local in entryAssigned[index][^1].Assigned) {
          if (!scoped.Contains(local))
            continue;
          var key = (depth, local);
          if (!backups.ContainsKey(key)) {
            var backup = new IrLocal($"s{depth.ToString(CultureInfo.InvariantCulture)}_{local.Name}", local.Type);
            backups.Add(key, backup);
            used.Add(local);
          }
        }
      }
      return backups;
    }

    private static IEnumerable<IrLocal> References(Instruction instruction) {
      var expression = instruction switch {
        Assignment assignment => assignment.Value,
        WriteLine write => write.Text,
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

    private IrSwitchSection Section(int index, int[] edges, IrLocal pc, IrLocal result, IReadOnlySet<IrLocal> used,
      Dictionary<int, int> entryDepths, Dictionary<int, List<(int FrameId, HashSet<IrLocal> Assigned)>> entryAssigned,
      Dictionary<(int Depth, IrLocal Local), IrLocal> backups) {
      IrAssign Go(int destination) => new(pc, Number(destination));
      IrStmt[] statements = instructions[index] switch {
        Assignment assignment when used.Contains(assignment.Destination) => [new IrAssign(assignment.Destination, assignment.Value), Go(edges[0])],
        Assignment => [Go(edges[0])],
        WriteLine write => [new IrWriteLine(write.Text), Go(edges[0])],
        Jump { Condition: { } condition } => [new IrIf(condition, new([Go(edges[0])]), new([Go(edges[1])]))],
        Jump => [Go(edges[0])],
        Exit exit => [new IrAssign(result, exit.Status), Go(-1)],
        ScopePush => [.. SaveStatements(entryDepths[index] + 1, entryAssigned[index][^1].Assigned, backups), Go(edges[0])],
        ScopePop => [.. RestoreStatements(entryDepths[index], entryAssigned[index][^2].Assigned, backups), Go(edges[0])],
        FrameUnwind unwind => [.. UnwindStatements(entryAssigned[index], unwind.FrameId, backups), Go(edges[0])],
        _ => throw new InvalidOperationException("unexpected Batch instruction")
      };
      return new([index], false, new(statements));
    }

    private static IEnumerable<IrStmt> SaveStatements(int depth, HashSet<IrLocal> outerAssigned,
      Dictionary<(int Depth, IrLocal Local), IrLocal> backups) {
      foreach (var local in outerAssigned)
        if (backups.TryGetValue((depth, local), out var backup))
          yield return new IrAssign(backup, new IrLocalRef(local));
    }

    private static IEnumerable<IrStmt> RestoreStatements(int depth, HashSet<IrLocal> outerAssigned,
      Dictionary<(int Depth, IrLocal Local), IrLocal> backups) {
      foreach (var local in outerAssigned)
        if (backups.TryGetValue((depth, local), out var backup))
          yield return new IrAssign(local, new IrLocalRef(backup));
    }

    private static IEnumerable<IrStmt> UnwindStatements(List<(int FrameId, HashSet<IrLocal> Assigned)> frames, int frameId,
      Dictionary<(int Depth, IrLocal Local), IrLocal> backups) {
      for (var level = frames.Count - 1; level > 0 && frames[level].FrameId == frameId; --level)
        foreach (var statement in RestoreStatements(level, frames[level - 1].Assigned, backups))
          yield return statement;
    }

    private static IrConst Number(int value) => new(IrType.I32, value);

  }

}
