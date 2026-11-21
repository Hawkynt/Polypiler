using System;
using System.Collections.Immutable;
using System.Linq;

namespace Polypiler.Core.MachineSemantics.Tracing;

/// <summary>How the events of two traces are compared.</summary>
public enum TraceMode {

  /// <summary>
  /// Memory writes are never compared as events, only through the final bytes of the observed ranges;
  /// device, external and trap events form one interleaved sequence. The mode for a high-level target.
  /// </summary>
  Semantic,

  /// <summary>Every observed kind, memory writes included, forms one interleaved sequence.</summary>
  Raw

}

/// <summary>A range of addresses: <paramref name="Length"/> bytes from <paramref name="Address"/>.</summary>
/// <param name="Address">The first address.</param>
/// <param name="Length">How many bytes; zero observes nothing.</param>
public sealed record AddressSpan(ulong Address, int Length);

/// <summary>What of a trace counts: the fixture's declaration of its observable state (PRD §23.1).</summary>
/// <param name="Registers">The registers compared, by name.</param>
/// <param name="Flags">The flags compared, by name.</param>
/// <param name="Memory">The ranges whose final bytes are compared.</param>
/// <param name="EventKinds">The event kinds compared, from <see cref="TraceEvent.Kinds"/>.</param>
/// <param name="Cycles">Whether the cycle count is compared.</param>
/// <param name="Mode">How events are compared.</param>
public sealed record TraceProjection(ImmutableArray<string> Registers, ImmutableArray<string> Flags, ImmutableArray<AddressSpan> Memory, ImmutableArray<string> EventKinds, bool Cycles, TraceMode Mode) {

  /// <summary>A projection that observes nothing, in <see cref="TraceMode.Semantic"/> mode.</summary>
  public static TraceProjection Nothing { get; } = new([], [], [], [], false, TraceMode.Semantic);

  /// <summary>Whether events of a kind are part of the compared sequence: listed, and not a memory write in <see cref="TraceMode.Semantic"/> mode.</summary>
  /// <param name="kind">The kind.</param>
  /// <returns>Whether they are compared.</returns>
  public bool Compares(string kind) => this.EventKinds.Contains(kind) && (this.Mode == TraceMode.Raw || kind != TraceEvent.MemoryWriteKind);

  /// <inheritdoc/>
  public bool Equals(TraceProjection? other)
    => other is not null && this.Registers.SequenceEqual(other.Registers) && this.Flags.SequenceEqual(other.Flags) && this.Memory.SequenceEqual(other.Memory)
      && this.EventKinds.SequenceEqual(other.EventKinds) && this.Cycles == other.Cycles && this.Mode == other.Mode;

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Registers.Length, this.Flags.Length, this.Memory.Length, this.EventKinds.Length, this.Cycles, this.Mode);

}
