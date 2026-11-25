using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;

namespace Polypiler.Core.Frontends.Msil;

/// <summary>A basic block in a decoded MSIL method's control-flow graph.</summary>
internal sealed class BasicBlock(int id, int startOffset, int endOffset, IReadOnlyList<IlInstruction> instructions) {

  /// <summary>Zero-based block index.</summary>
  public int Id { get; } = id;

  /// <summary>Byte offset of the first instruction in this block.</summary>
  public int StartOffset { get; } = startOffset;

  /// <summary>Byte offset immediately following the last instruction in this block.</summary>
  public int EndOffset { get; } = endOffset;

  /// <summary>The instructions in execution order.</summary>
  public IReadOnlyList<IlInstruction> Instructions { get; } = instructions;

  /// <summary>The last instruction in this block.</summary>
  public IlInstruction Last => this.Instructions[^1];

  /// <summary>Direct control-flow successor block IDs.</summary>
  public List<int> Successors { get; } = [];

  /// <summary>Direct control-flow predecessor block IDs.</summary>
  public List<int> Predecessors { get; } = [];

  /// <summary>Verified evaluation stack types on entry to this block, or <see langword="null"/> if unreachable.</summary>
  public List<MsilType>? EntryStack { get; set; }

}

/// <summary>
/// A protected <c>try</c> range paired with all exception handlers attached to the exact same
/// <c>[TryOffset, TryEnd)</c> interval, plus nested child groups.
/// </summary>
internal sealed class TryGroup(int tryOffset, int tryEnd) {

  /// <summary>Start byte offset of the protected <c>try</c> region.</summary>
  public int TryOffset { get; } = tryOffset;

  /// <summary>Exclusive end byte offset of the protected <c>try</c> region.</summary>
  public int TryEnd { get; } = tryEnd;

  /// <summary>Handlers attached to this protected region, in metadata table order.</summary>
  public List<IlExceptionRegion> Handlers { get; } = [];

  /// <summary>Nested <see cref="TryGroup"/>s inside the <c>try</c> body.</summary>
  public List<TryGroup> TryChildren { get; } = [];

  /// <summary>Nested <see cref="TryGroup"/>s inside each handler, keyed by handler index.</summary>
  public Dictionary<int, List<TryGroup>> HandlerChildren { get; } = [];

  /// <summary>Lowest byte offset spanned by this group (start of try).</summary>
  public int SpanStart => this.TryOffset;

  /// <summary>Exclusive end byte offset spanned by this group (end of last handler).</summary>
  public int SpanEnd => this.Handlers.Count == 0 ? this.TryEnd : Math.Max(this.TryEnd, this.Handlers.Max(h => h.HandlerEnd));

}

/// <summary>Basic-block CFG and nested exception-handling tree for a single MSIL method.</summary>
internal sealed class ControlFlowGraph {

  private ControlFlowGraph(
    IReadOnlyList<BasicBlock> blocks,
    IReadOnlyDictionary<int, int> offsetToBlock,
    IReadOnlyList<TryGroup> topLevelTryGroups,
    IReadOnlyList<IlExceptionRegion> regions) {
    this.Blocks = blocks;
    this.OffsetToBlock = offsetToBlock;
    this.TopLevelTryGroups = topLevelTryGroups;
    this.Regions = regions;
  }

  /// <summary>All basic blocks ordered by <see cref="BasicBlock.StartOffset"/>.</summary>
  public IReadOnlyList<BasicBlock> Blocks { get; }

  /// <summary>Maps an instruction start byte offset to its owning basic block ID.</summary>
  public IReadOnlyDictionary<int, int> OffsetToBlock { get; }

  /// <summary>Top-level exception-handling groups in the method.</summary>
  public IReadOnlyList<TryGroup> TopLevelTryGroups { get; }

  /// <summary>Flat list of decoded exception regions.</summary>
  public IReadOnlyList<IlExceptionRegion> Regions { get; }

  /// <summary>Builds a validated CFG from decoded instructions and exception regions.</summary>
  public static ControlFlowGraph Build(
    IReadOnlyList<IlInstruction> instructions,
    IReadOnlyList<IlExceptionRegion> regions,
    string methodName) {
    if (instructions.Count == 0)
      throw new MsilImportException(MsilFrontend.CorruptMetadata, $"method '{methodName}' has an empty IL body");

    var offsetToIndex = new Dictionary<int, int>(instructions.Count);
    for (var i = 0; i < instructions.Count; ++i)
      offsetToIndex[instructions[i].Offset] = i;

    var ilEnd = instructions[^1].NextOffset;
    var leaders = new SortedSet<int> { instructions[0].Offset };

    void RequireOffset(int offset, string what) {
      if (!offsetToIndex.ContainsKey(offset))
        throw new MsilImportException(MsilFrontend.UnverifiableStack, $"{what} IL_{offset:X4} does not target an instruction boundary in '{methodName}'");

      leaders.Add(offset);
    }

    foreach (var ins in instructions) {
      if (ins.OperandKind == IlOperandKind.BranchTarget)
        RequireOffset((int)ins.IntValue, "branch target");
      else if (ins.OperandKind == IlOperandKind.Switch && ins.SwitchTargets is not null)
        foreach (var target in ins.SwitchTargets)
          RequireOffset(target, "switch target");

      if (ins.IsBlockTerminator && ins.NextOffset < ilEnd)
        leaders.Add(ins.NextOffset);
    }

    foreach (var region in regions) {
      RequireOffset(region.TryOffset, "exception try start");
      if (region.TryEnd < ilEnd)
        RequireOffset(region.TryEnd, "exception try end");

      RequireOffset(region.HandlerOffset, "exception handler start");
      if (region.HandlerEnd < ilEnd)
        RequireOffset(region.HandlerEnd, "exception handler end");

      if (region.Kind == ExceptionRegionKind.Filter)
        RequireOffset(region.FilterOffset, "exception filter start");
    }

    var leaderList = leaders.ToList();
    var blocks = new List<BasicBlock>(leaderList.Count);
    var offsetToBlock = new Dictionary<int, int>(leaderList.Count);

    for (var b = 0; b < leaderList.Count; ++b) {
      var startOffset = leaderList[b];
      var endOffset = b + 1 < leaderList.Count ? leaderList[b + 1] : ilEnd;
      var startIdx = offsetToIndex[startOffset];
      var list = new List<IlInstruction>();
      for (var i = startIdx; i < instructions.Count && instructions[i].Offset < endOffset; ++i)
        list.Add(instructions[i]);

      blocks.Add(new(b, startOffset, endOffset, list));
      offsetToBlock[startOffset] = b;
    }

    void AddEdge(int from, int to) {
      if (!blocks[from].Successors.Contains(to)) {
        blocks[from].Successors.Add(to);
        blocks[to].Predecessors.Add(from);
      }
    }

    for (var b = 0; b < blocks.Count; ++b) {
      var last = blocks[b].Last;
      if (last.OperandKind == IlOperandKind.BranchTarget)
        AddEdge(b, offsetToBlock[(int)last.IntValue]);
      else if (last.OperandKind == IlOperandKind.Switch && last.SwitchTargets is not null)
        foreach (var target in last.SwitchTargets)
          AddEdge(b, offsetToBlock[target]);

      if (last.FallsThrough) {
        if (b + 1 >= blocks.Count)
          throw new MsilImportException(MsilFrontend.UnverifiableStack, $"control falls off the end of method '{methodName}'");

        AddEdge(b, b + 1);
      }
    }

    var topGroups = BuildTryTree(regions);
    return new(blocks, offsetToBlock, topGroups, regions);
  }

  private static List<TryGroup> BuildTryTree(IReadOnlyList<IlExceptionRegion> regions) {
    if (regions.Count == 0)
      return [];

    var groups = new List<TryGroup>();
    foreach (var region in regions) {
      var existing = groups.Find(g => g.TryOffset == region.TryOffset && g.TryEnd == region.TryEnd);
      if (existing is null) {
        existing = new(region.TryOffset, region.TryEnd);
        groups.Add(existing);
      }

      existing.Handlers.Add(region);
    }

    // Sort outer (larger full span, or earlier start) before inner so nested groups inside
    // either try bodies or catch/finally handlers always find their enclosing parent first.
    groups.Sort((a, b) => {
      var fullCmp = (b.SpanEnd - b.SpanStart).CompareTo(a.SpanEnd - a.SpanStart);
      return fullCmp != 0 ? fullCmp : a.TryOffset.CompareTo(b.TryOffset);
    });

    var topLevel = new List<TryGroup>();
    foreach (var group in groups)
      InsertGroup(topLevel, group);

    return topLevel;
  }

  private static void InsertGroup(List<TryGroup> siblings, TryGroup candidate) {
    foreach (var parent in siblings) {
      if (candidate.TryOffset >= parent.TryOffset && candidate.SpanEnd <= parent.TryEnd) {
        InsertGroup(parent.TryChildren, candidate);
        return;
      }

      for (var i = 0; i < parent.Handlers.Count; ++i) {
        var h = parent.Handlers[i];
        var hStart = h.Kind == ExceptionRegionKind.Filter ? Math.Min(h.FilterOffset, h.HandlerOffset) : h.HandlerOffset;
        if (candidate.TryOffset >= hStart && candidate.SpanEnd <= h.HandlerEnd) {
          if (!parent.HandlerChildren.TryGetValue(i, out var children)) {
            children = [];
            parent.HandlerChildren[i] = children;
          }

          InsertGroup(children, candidate);
          return;
        }
      }
    }

    siblings.Add(candidate);
  }

}
