using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Jvm.Lowering;

internal abstract record JvmTerminator;

internal sealed record JvmJump(int Target) : JvmTerminator;

internal sealed record JvmBranch(IrExpr Condition, int TrueTarget, int FalseTarget) : JvmTerminator;

internal sealed record JvmSwitchTerm(IrExpr Key, IReadOnlyList<(long Key, int Target)> Cases, int DefaultTarget) : JvmTerminator;

internal sealed record JvmReturnTerm(IrExpr? Value) : JvmTerminator;

internal sealed record JvmThrowTerm(IrExpr Exception) : JvmTerminator;

internal sealed class JvmBasicBlock(int startPc) {

  public int StartPc { get; } = startPc;
  public List<IrStmt> Statements { get; } = [];
  public JvmTerminator? Terminator { get; set; }

  public IEnumerable<int> Successors() => this.Terminator switch {
    JvmJump j => [j.Target],
    JvmBranch b => [b.TrueTarget, b.FalseTarget],
    JvmSwitchTerm s => s.Cases.Select(c => c.Target).Append(s.DefaultTarget).Distinct(),
    _ => []
  };

}

internal sealed record JvmTryRegion(int StartPc, int EndPc, IReadOnlyList<(IrClassType CatchClass, int HandlerPc, IrLocal Stack0Local)> Handlers);

internal sealed class JvmControlFlowStructurer {

  private readonly Dictionary<int, JvmBasicBlock> _blocks;
  private readonly IReadOnlyList<JvmTryRegion> _tryRegions;
  private readonly List<IrLocal> _prologueLocals;
  private readonly List<IrLocal> _catchLocals;
  private readonly Dictionary<int, HashSet<int>> _dominators;
  private readonly Dictionary<int, int> _immediatePostDom;
  private int _counter;

  public JvmControlFlowStructurer(
    Dictionary<int, JvmBasicBlock> blocks,
    IReadOnlyList<JvmTryRegion> tryRegions,
    List<IrLocal> prologueLocals,
    List<IrLocal> catchLocals) {
    this._blocks = blocks;
    this._tryRegions = tryRegions;
    this._prologueLocals = prologueLocals;
    this._catchLocals = catchLocals;
    this._dominators = ComputeDominators(blocks);
    this._immediatePostDom = ComputeImmediatePostDominators(blocks);
  }

  public List<IrStmt> Structure(int entryPc)
    => this.StructureRegion(entryPc, stopPc: -1, breakTarget: -1, continueTarget: -1, inSwitch: false, []);

  private List<IrStmt> StructureRegion(
    int startPc,
    int stopPc,
    int breakTarget,
    int continueTarget,
    bool inSwitch,
    HashSet<(int, int)> activeTries) {
    var result = new List<IrStmt>();
    var pc = startPc;
    var visited = new HashSet<int>();
    while (pc >= 0 && pc != stopPc) {
      if (pc == breakTarget && !inSwitch) {
        result.Add(new IrBreak());
        return result;
      }

      if (pc == continueTarget) {
        result.Add(new IrContinue());
        return result;
      }

      if (!this._blocks.TryGetValue(pc, out var block))
        return result;

      var tryRegion = this.FindOutermostTryAt(pc, activeTries);
      if (tryRegion is not null) {
        activeTries.Add((tryRegion.StartPc, tryRegion.EndPc));
        var afterTry = this.FindAfterTry(tryRegion, stopPc);
        var tryStop = afterTry >= 0 ? afterTry : tryRegion.EndPc;
        var tryBody = this.StructureRegion(tryRegion.StartPc, tryStop, breakTarget, continueTarget, inSwitch, activeTries);
        var catches = new List<IrCatch>(tryRegion.Handlers.Count);
        foreach (var (catchClass, handlerPc, stack0Local) in tryRegion.Handlers) {
          var catchLocal = new IrLocal($"__ex_{handlerPc}_{this._counter++}", IrType.Object(catchClass));
          this._catchLocals.Add(catchLocal);
          var handlerStmts = new List<IrStmt> { new IrAssign(stack0Local, new IrLocalRef(catchLocal)) };
          handlerStmts.AddRange(this.StructureRegion(handlerPc, afterTry, breakTarget, continueTarget, inSwitch, activeTries));
          catches.Add(new IrCatch(catchClass, catchLocal, null, new IrBlock(handlerStmts)));
        }

        activeTries.Remove((tryRegion.StartPc, tryRegion.EndPc));
        result.Add(new IrTry(new IrBlock(tryBody), catches, null));
        pc = afterTry;
        continue;
      }

      var loop = this.FindNaturalLoop(pc, stopPc);
      if (loop is not null && !visited.Contains(pc)) {
        visited.Add(pc);
        var (bodySet, exits) = loop.Value;
        if (exits.Count <= 1 && !this.ContainsSwitchExitingLoop(bodySet, exits)) {
          var exitPc = exits.Count == 1 ? exits[0] : -1;
          result.Add(this.BuildWhileLoop(pc, bodySet, exitPc, activeTries));
          pc = exitPc;
          continue;
        }

        result.AddRange(this.BuildDispatcher(pc, stopPc));
        return result;
      }

      if (!visited.Add(pc)) {
        result.AddRange(this.BuildDispatcher(pc, stopPc));
        return result;
      }

      result.AddRange(block.Statements);
      switch (block.Terminator) {
        case JvmReturnTerm ret:
          result.Add(new IrReturn(ret.Value));
          return result;
        case JvmThrowTerm thr:
          result.Add(new IrThrow(thr.Exception));
          return result;
        case JvmJump jmp:
          pc = jmp.Target;
          break;
        case JvmBranch br: {
          var merge = this.ChooseMerge(pc, stopPc);
          if (br.TrueTarget == merge && br.FalseTarget != merge) {
            var thenStmts = this.StructureRegion(br.FalseTarget, merge, breakTarget, continueTarget, inSwitch, activeTries);
            result.Add(new IrIf(new IrUnary(IrUnaryOp.LogicalNot, br.Condition), new IrBlock(thenStmts), null));
          } else if (br.FalseTarget == merge && br.TrueTarget != merge) {
            var thenStmts = this.StructureRegion(br.TrueTarget, merge, breakTarget, continueTarget, inSwitch, activeTries);
            result.Add(new IrIf(br.Condition, new IrBlock(thenStmts), null));
          } else {
            var thenStmts = this.StructureRegion(br.TrueTarget, merge, breakTarget, continueTarget, inSwitch, activeTries);
            var elseStmts = this.StructureRegion(br.FalseTarget, merge, breakTarget, continueTarget, inSwitch, activeTries);
            result.Add(new IrIf(br.Condition, new IrBlock(thenStmts), elseStmts.Count > 0 ? new IrBlock(elseStmts) : null));
          }

          pc = merge;
          break;
        }
        case JvmSwitchTerm sw: {
          var merge = this.ChooseMerge(pc, stopPc);
          var byTarget = new Dictionary<int, List<long>>();
          var order = new List<int>();
          foreach (var (key, target) in sw.Cases) {
            if (!byTarget.TryGetValue(target, out var keys)) {
              keys = [];
              byTarget[target] = keys;
              order.Add(target);
            }

            keys.Add(key);
          }

          var sections = new List<IrSwitchSection>();
          var defaultEmitted = false;
          foreach (var target in order) {
            var isDefault = target == sw.DefaultTarget;
            if (isDefault)
              defaultEmitted = true;

            var body = this.StructureRegion(target, merge, breakTarget, continueTarget, inSwitch: true, activeTries);
            sections.Add(new IrSwitchSection(byTarget[target], isDefault, new IrBlock(body)));
          }

          if (!defaultEmitted) {
            var defaultBody = this.StructureRegion(sw.DefaultTarget, merge, breakTarget, continueTarget, inSwitch: true, activeTries);
            sections.Add(new IrSwitchSection([], true, new IrBlock(defaultBody)));
          }

          result.Add(new IrSwitch(sw.Key, sections));
          pc = merge;
          break;
        }
      }
    }

    return result;
  }

  private IrStmt BuildWhileLoop(int headerPc, HashSet<int> bodySet, int exitPc, HashSet<(int, int)> activeTries) {
    var header = this._blocks[headerPc];
    if (header.Statements.Count == 0 && header.Terminator is JvmBranch br) {
      if (br.FalseTarget == exitPc && bodySet.Contains(br.TrueTarget)) {
        var body = this.StructureRegion(br.TrueTarget, stopPc: headerPc, breakTarget: exitPc, continueTarget: headerPc, inSwitch: false, activeTries);
        return new IrWhile(br.Condition, new IrBlock(body));
      }

      if (br.TrueTarget == exitPc && bodySet.Contains(br.FalseTarget)) {
        var body = this.StructureRegion(br.FalseTarget, stopPc: headerPc, breakTarget: exitPc, continueTarget: headerPc, inSwitch: false, activeTries);
        return new IrWhile(new IrUnary(IrUnaryOp.LogicalNot, br.Condition), new IrBlock(body));
      }
    }

    var loopBody = new List<IrStmt>(header.Statements);
    if (header.Terminator is JvmBranch headerBranch) {
      var exitTrue = headerBranch.TrueTarget == exitPc;
      var exitCond = exitTrue ? headerBranch.Condition : new IrUnary(IrUnaryOp.LogicalNot, headerBranch.Condition);
      var nextPc = exitTrue ? headerBranch.FalseTarget : headerBranch.TrueTarget;
      loopBody.Add(new IrIf(exitCond, new IrBlock([new IrBreak()]), null));
      loopBody.AddRange(this.StructureRegion(nextPc, stopPc: headerPc, breakTarget: exitPc, continueTarget: headerPc, inSwitch: false, activeTries));
    } else if (header.Terminator is JvmJump jmp) {
      loopBody.AddRange(this.StructureRegion(jmp.Target, stopPc: headerPc, breakTarget: exitPc, continueTarget: headerPc, inSwitch: false, activeTries));
    }

    return new IrWhile(new IrConst(IrType.Bool, 1), new IrBlock(loopBody));
  }

  private List<IrStmt> BuildDispatcher(int entryPc, int stopPc) {
    var reachable = new List<int>();
    var seen = new HashSet<int> { entryPc };
    var queue = new Queue<int>();
    queue.Enqueue(entryPc);
    while (queue.Count > 0) {
      var cur = queue.Dequeue();
      if (cur == stopPc || !this._blocks.TryGetValue(cur, out var blk))
        continue;

      reachable.Add(cur);
      foreach (var succ in blk.Successors())
        if (succ != stopPc && seen.Add(succ))
          queue.Enqueue(succ);
    }

    reachable.Sort();
    var pcLocal = new IrLocal($"__pc_{this._counter++}", IrType.I32);
    this._prologueLocals.Add(pcLocal);
    var sections = new List<IrSwitchSection>(reachable.Count + 1);
    foreach (var pc in reachable) {
      var blk = this._blocks[pc];
      var stmts = new List<IrStmt>(blk.Statements);
      switch (blk.Terminator) {
        case JvmJump jmp:
          stmts.Add(new IrAssign(pcLocal, new IrConst(IrType.I32, jmp.Target)));
          break;
        case JvmBranch br:
          stmts.Add(new IrAssign(pcLocal, new IrConditional(br.Condition, new IrConst(IrType.I32, br.TrueTarget), new IrConst(IrType.I32, br.FalseTarget))));
          break;
        case JvmSwitchTerm sw: {
          var sub = new List<IrSwitchSection>();
          var hasDefault = false;
          foreach (var g in sw.Cases.GroupBy(c => c.Target)) {
            var isDef = g.Key == sw.DefaultTarget;
            if (isDef)
              hasDefault = true;

            sub.Add(new IrSwitchSection([.. g.Select(x => x.Key)], isDef, new IrBlock([new IrAssign(pcLocal, new IrConst(IrType.I32, g.Key))])));
          }

          if (!hasDefault)
            sub.Add(new IrSwitchSection([], true, new IrBlock([new IrAssign(pcLocal, new IrConst(IrType.I32, sw.DefaultTarget))])));

          stmts.Add(new IrSwitch(sw.Key, sub));
          break;
        }
        case JvmReturnTerm ret:
          stmts.Add(new IrReturn(ret.Value));
          break;
        case JvmThrowTerm thr:
          stmts.Add(new IrThrow(thr.Exception));
          break;
      }

      sections.Add(new IrSwitchSection([pc], false, new IrBlock(stmts)));
    }

    sections.Add(new IrSwitchSection([], true, new IrBlock([new IrAssign(pcLocal, new IrConst(IrType.I32, stopPc))])));
    var loop = new IrWhile(
      new IrBinary(IrBinaryOp.NotEqual, new IrLocalRef(pcLocal), new IrConst(IrType.I32, stopPc)),
      new IrBlock([new IrSwitch(new IrLocalRef(pcLocal), sections)]));
    return [new IrAssign(pcLocal, new IrConst(IrType.I32, entryPc)), loop];
  }

  private JvmTryRegion? FindOutermostTryAt(int pc, HashSet<(int, int)> activeTries) {
    JvmTryRegion? best = null;
    foreach (var region in this._tryRegions)
      if (region.StartPc == pc && !activeTries.Contains((region.StartPc, region.EndPc)) && (best is null || region.EndPc > best.EndPc))
        best = region;

    return best;
  }

  private int FindAfterTry(JvmTryRegion region, int stopPc) {
    var handlerStarts = region.Handlers.Select(h => h.HandlerPc).ToHashSet();
    var exits = new HashSet<int>();
    foreach (var (pc, block) in this._blocks)
      if (pc >= region.StartPc && pc < region.EndPc)
        foreach (var succ in block.Successors())
          if ((succ < region.StartPc || succ >= region.EndPc) && !handlerStarts.Contains(succ))
            exits.Add(succ);

    return exits.Count == 1 ? exits.First() : stopPc;
  }

  private (HashSet<int> Body, List<int> Exits)? FindNaturalLoop(int headerPc, int stopPc) {
    var backSources = new List<int>();
    foreach (var (pc, block) in this._blocks)
      if (pc != stopPc && block.Successors().Contains(headerPc) && this._dominators[pc].Contains(headerPc))
        backSources.Add(pc);

    if (backSources.Count == 0)
      return null;

    var preds = this._blocks.Keys.ToDictionary(pc => pc, _ => new List<int>());
    foreach (var (pc, block) in this._blocks)
      foreach (var succ in block.Successors())
        if (preds.TryGetValue(succ, out var list))
          list.Add(pc);

    var body = new HashSet<int> { headerPc };
    var work = new Stack<int>();
    foreach (var src in backSources)
      if (body.Add(src))
        work.Push(src);

    while (work.Count > 0) {
      var node = work.Pop();
      foreach (var p in preds[node])
        if (body.Add(p))
          work.Push(p);
    }

    var exits = new HashSet<int>();
    foreach (var node in body)
      foreach (var succ in this._blocks[node].Successors())
        if (!body.Contains(succ))
          exits.Add(succ);

    return (body, exits.OrderBy(x => x).ToList());
  }

  private bool ContainsSwitchExitingLoop(HashSet<int> bodySet, List<int> exits) {
    if (exits.Count != 1)
      return false;

    var exitPc = exits[0];
    foreach (var pc in bodySet) {
      if (this._blocks[pc].Terminator is not JvmSwitchTerm)
        continue;

      var seen = new HashSet<int>();
      var stack = new Stack<int>(this._blocks[pc].Successors());
      while (stack.Count > 0) {
        var cur = stack.Pop();
        if (cur == exitPc)
          return true;

        if (!bodySet.Contains(cur) || !seen.Add(cur))
          continue;

        foreach (var succ in this._blocks[cur].Successors())
          stack.Push(succ);
      }
    }

    return false;
  }

  private int ChooseMerge(int branchPc, int stopPc)
    => this._immediatePostDom.TryGetValue(branchPc, out var ipdom) && ipdom >= 0 ? ipdom : stopPc;

  private static Dictionary<int, HashSet<int>> ComputeDominators(Dictionary<int, JvmBasicBlock> blocks) {
    var all = blocks.Keys.ToHashSet();
    var preds = blocks.Keys.ToDictionary(pc => pc, _ => new List<int>());
    foreach (var (pc, block) in blocks)
      foreach (var succ in block.Successors())
        if (preds.TryGetValue(succ, out var list))
          list.Add(pc);

    var dom = new Dictionary<int, HashSet<int>>();
    foreach (var pc in all)
      dom[pc] = pc == 0 || preds[pc].Count == 0 ? [pc] : [.. all];

    var changed = true;
    while (changed) {
      changed = false;
      foreach (var pc in all) {
        if (pc == 0 || preds[pc].Count == 0)
          continue;

        HashSet<int>? next = null;
        foreach (var p in preds[pc])
          if (next is null)
            next = [.. dom[p]];
          else
            next.IntersectWith(dom[p]);

        next!.Add(pc);
        if (!next.SetEquals(dom[pc])) {
          dom[pc] = next;
          changed = true;
        }
      }
    }

    return dom;
  }

  private static Dictionary<int, int> ComputeImmediatePostDominators(Dictionary<int, JvmBasicBlock> blocks) {
    const int virtualExit = -1;
    var all = blocks.Keys.Append(virtualExit).ToHashSet();
    var postDom = new Dictionary<int, HashSet<int>>();
    foreach (var pc in all)
      postDom[pc] = pc == virtualExit ? [virtualExit] : [.. all];

    var changed = true;
    while (changed) {
      changed = false;
      foreach (var (pc, block) in blocks) {
        var succs = block.Successors().Where(blocks.ContainsKey).ToList();
        if (succs.Count == 0)
          succs.Add(virtualExit);

        HashSet<int>? next = null;
        foreach (var s in succs)
          if (next is null)
            next = [.. postDom[s]];
          else
            next.IntersectWith(postDom[s]);

        next!.Add(pc);
        if (!next.SetEquals(postDom[pc])) {
          postDom[pc] = next;
          changed = true;
        }
      }
    }

    var ipdom = new Dictionary<int, int>();
    foreach (var pc in blocks.Keys) {
      var strict = postDom[pc].Where(d => d != pc).ToList();
      var best = strict[0];
      var bestCount = postDom[best].Count;
      for (var i = 1; i < strict.Count; ++i) {
        var candidate = strict[i];
        var count = postDom[candidate].Count;
        if (count > bestCount) {
          best = candidate;
          bestCount = count;
        }
      }

      ipdom[pc] = best;
    }

    return ipdom;
  }

}
