using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Polypiler.Core.MachineSemantics;

/// <summary>
/// The machine state a routine exchanges with its caller: the root registers and flags it may read
/// before it writes them on some path (its inputs) and those it writes on some path (its outputs). The
/// raise to the tree IR makes each root register and flag a local variable, takes the inputs as
/// parameters and returns the outputs; this replaces the <c>LiveIn</c> and <c>LiveOut</c> markers an SSA
/// form would need. A sub-register access counts for its root; a write that preserves bits of its root
/// also reads it.
/// </summary>
/// <param name="RegistersIn">Root registers read before written on some path, in profile order.</param>
/// <param name="RegistersOut">Root registers written on some path, in profile order.</param>
/// <param name="FlagsIn">Flags read before written on some path, in profile order.</param>
/// <param name="FlagsOut">Flags written or undefined on some path, in profile order.</param>
public sealed record MachineInterface(ImmutableArray<string> RegistersIn, ImmutableArray<string> RegistersOut, ImmutableArray<string> FlagsIn, ImmutableArray<string> FlagsOut) {

  /// <summary>Computes the interface of a verified function.</summary>
  /// <param name="function">The function.</param>
  /// <param name="profile">Its machine profile.</param>
  /// <returns>The interface.</returns>
  public static MachineInterface Of(MachineFunction function, IsaProfile profile) {
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(profile);

    // Per block: what it reads before writing it itself (use) and what it writes (def), by storage name.
    var use = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    var def = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    var written = new HashSet<string>(StringComparer.Ordinal);
    foreach (var block in function.Blocks) {
      var blockUse = use[block.Label] = new(StringComparer.Ordinal);
      var blockDef = def[block.Label] = new(StringComparer.Ordinal);
      foreach (var instruction in block.Instructions) {
        var (reads, writes) = Access(instruction, profile);
        foreach (var name in reads.Where(name => !blockDef.Contains(name)))
          blockUse.Add(name);
        foreach (var name in writes) {
          blockDef.Add(name);
          written.Add(name);
        }
      }
    }

    // Backward liveness to the entry: live-in(b) = use(b) ∪ (live-out(b) − def(b)).
    var liveIn = function.Blocks.ToDictionary(block => block.Label, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
    for (var changed = true; changed;) {
      changed = false;
      foreach (var block in function.Blocks.Reverse()) {
        var live = new HashSet<string>(use[block.Label], StringComparer.Ordinal);
        foreach (var successor in block.Terminator?.Successors ?? [])
          if (liveIn.TryGetValue(successor, out var after))
            live.UnionWith(after.Where(name => !def[block.Label].Contains(name)));
        if (live.SetEquals(liveIn[block.Label]))
          continue;
        liveIn[block.Label] = live;
        changed = true;
      }
    }

    var entry = function.Blocks.IsEmpty ? [] : liveIn[function.Blocks[0].Label];
    var roots = profile.Registers.Where(register => register.IsRoot).Select(register => register.Name).ToList();
    var flags = profile.Flags.Select(flag => flag.Name).ToList();
    return new(
      [.. roots.Where(entry.Contains)], [.. roots.Where(written.Contains)],
      [.. flags.Where(entry.Contains)], [.. flags.Where(written.Contains)]);
  }

  /// <summary>The root registers and flags an instruction reads and writes.</summary>
  private static (IEnumerable<string> Reads, IEnumerable<string> Writes) Access(MachineInstruction instruction, IsaProfile profile) {
    if (instruction.Storage is not { } name)
      return ([], []);
    switch (instruction.Opcode) {
      case MachineOpcode.FlagRead:
        return ([name], []);
      case MachineOpcode.FlagWrite or MachineOpcode.FlagUndef:
        return ([], [name]);
    }

    var register = profile.Register(name)!;
    var root = profile.Root(register).Name;
    return instruction.Opcode == MachineOpcode.RegRead
      ? ([root], [])
      : (profile.ReplacesRoot(register) ? [] : [root], [root]);
  }

}
