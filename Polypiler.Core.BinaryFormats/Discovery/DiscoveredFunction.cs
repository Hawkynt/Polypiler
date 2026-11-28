using System.Collections.Immutable;
using System.Linq;
using Polypiler.Core.MachineSemantics.Ports;

namespace Polypiler.Core.BinaryFormats.Discovery;

/// <summary>How sure discovery is that an address is the entry of a function.</summary>
public enum Confidence {

  /// <summary>The manifest or the container says so: an entry or a function symbol.</summary>
  Proven,

  /// <summary>A call from a traversed function reaches it.</summary>
  Derived,

  /// <summary>Only a heuristic candidate, such as a prologue scan, suggests it.</summary>
  Heuristic

}

/// <summary>
/// A basic block: instructions that run one after the other, entered at the first only. A call does not
/// end a block; a jump, a conditional jump, an indirect jump, a return, a trap and an instruction that
/// does not decode do.
/// </summary>
/// <param name="Start">The address of its first byte.</param>
/// <param name="End">The address of its last byte.</param>
/// <param name="Instructions">The instructions, by address.</param>
/// <param name="Successors">The blocks of the same function control may go to next, by address.</param>
public sealed record DiscoveredBlock(ulong Start, ulong End, ImmutableArray<DisassembledInstruction> Instructions, ImmutableArray<ulong> Successors) {

  /// <summary>Whether the block ends in an indirect jump whose targets no resolver proved.</summary>
  public bool Unresolved { get; init; }

  /// <summary>The function the block's last jump enters as a tail call, or <see langword="null"/>.</summary>
  public ulong? TailCall { get; init; }

  /// <summary>Targets of its jumps and calls outside the code: imports, or what <c>PLY4073</c> reports.</summary>
  public ImmutableArray<ulong> ExternalTargets { get; init; } = [];

  /// <summary>Whether the block holds an indirect call, whose callee stays unresolved.</summary>
  public bool UnresolvedCall { get; init; }

  /// <summary>The address after its last instruction where the bytes do not decode, or <see langword="null"/>.</summary>
  public ulong? Invalid { get; init; }

  /// <inheritdoc/>
  public bool Equals(DiscoveredBlock? other)
    => other is not null && this.Start == other.Start && this.End == other.End && this.Instructions.SequenceEqual(other.Instructions)
      && this.Successors.SequenceEqual(other.Successors) && this.Unresolved == other.Unresolved && this.TailCall == other.TailCall
      && this.ExternalTargets.SequenceEqual(other.ExternalTargets) && this.UnresolvedCall == other.UnresolvedCall && this.Invalid == other.Invalid;

  /// <inheritdoc/>
  public override int GetHashCode() => System.HashCode.Combine(this.Start, this.End, this.Instructions.Length);

}

/// <summary>A function discovery found: its entry, why it is one, and the blocks reachable from the entry.</summary>
/// <param name="Entry">The entry address.</param>
/// <param name="Name">The name the manifest or a symbol gives, or <see langword="null"/>.</param>
/// <param name="Confidence">How sure discovery is that it is a function.</param>
/// <param name="Evidence">What says so: <c>manifest</c>, <c>entry</c>, <c>symbol</c>, <c>call from &lt;address&gt;</c> or <c>hint</c>.</param>
/// <param name="Blocks">The blocks, by address.</param>
/// <param name="Complete">Whether every path was followed to its end: no undecodable byte, no conflict, no exhausted budget.</param>
public sealed record DiscoveredFunction(ulong Entry, string? Name, Confidence Confidence, string Evidence, ImmutableArray<DiscoveredBlock> Blocks, bool Complete) {

  /// <inheritdoc/>
  public bool Equals(DiscoveredFunction? other)
    => other is not null && this.Entry == other.Entry && this.Name == other.Name && this.Confidence == other.Confidence && this.Evidence == other.Evidence
      && this.Blocks.SequenceEqual(other.Blocks) && this.Complete == other.Complete;

  /// <inheritdoc/>
  public override int GetHashCode() => System.HashCode.Combine(this.Entry, this.Confidence, this.Blocks.Length);

}

/// <summary>What discovery found.</summary>
/// <param name="Functions">The functions, by entry address.</param>
/// <param name="Bytes">The class of every byte.</param>
/// <param name="Diagnostics">The diagnostics, in the order they arose.</param>
public sealed record DiscoveryResult(ImmutableArray<DiscoveredFunction> Functions, ByteMap Bytes, ImmutableArray<Diagnostic> Diagnostics) {

  /// <inheritdoc/>
  public bool Equals(DiscoveryResult? other)
    => other is not null && this.Functions.SequenceEqual(other.Functions) && this.Bytes.Equals(other.Bytes) && this.Diagnostics.SequenceEqual(other.Diagnostics);

  /// <inheritdoc/>
  public override int GetHashCode() => System.HashCode.Combine(this.Functions.Length, this.Diagnostics.Length);

}

/// <summary>How far discovery goes.</summary>
/// <param name="MaxInstructions">The most instructions it decodes; one more is <c>PLY4074</c>.</param>
/// <param name="AcceptHeuristic">Whether heuristic candidates (<see cref="LoadedBinary.Hints"/>) are traversed; the manifest's <c>acceptHeuristicFunctions</c>.</param>
public sealed record DiscoveryOptions(long MaxInstructions = 1_000_000, bool AcceptHeuristic = false);
