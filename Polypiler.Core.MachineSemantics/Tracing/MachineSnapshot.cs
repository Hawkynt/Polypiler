using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Polypiler.Core.MachineSemantics.Tracing;

/// <summary>Bytes of memory starting at an address.</summary>
/// <param name="Address">The address of the first byte.</param>
/// <param name="Bytes">The bytes.</param>
public sealed record MemoryBytes(ulong Address, ImmutableArray<byte> Bytes) {

  /// <inheritdoc/>
  public bool Equals(MemoryBytes? other) => other is not null && this.Address == other.Address && this.Bytes.SequenceEqual(other.Bytes);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Address, this.Bytes.Length);

}

/// <summary>
/// The state of a machine at one moment: register values by register name (roots or sub-registers of
/// the profile, at most 64 bits each, so a value is a <see cref="ulong"/>), flags by name and blocks
/// of memory.
/// </summary>
/// <param name="Registers">The register values.</param>
/// <param name="Flags">The flags; <see langword="null"/> is an undefined flag.</param>
/// <param name="Memory">The blocks of memory; where two overlap, the later one wins.</param>
public sealed record MachineSnapshot(ImmutableDictionary<string, ulong> Registers, ImmutableDictionary<string, bool?> Flags, ImmutableArray<MemoryBytes> Memory) {

  /// <summary>A snapshot with no registers, flags or memory.</summary>
  public static MachineSnapshot Empty { get; } = new(ImmutableDictionary<string, ulong>.Empty, ImmutableDictionary<string, bool?>.Empty, []);

  /// <summary>The byte at an address, or <see langword="null"/> when no block holds it.</summary>
  /// <param name="address">The address.</param>
  /// <returns>The byte.</returns>
  public byte? ByteAt(ulong address) {
    for (var index = this.Memory.Length - 1; index >= 0; --index) {
      var block = this.Memory[index];
      if (address >= block.Address && address - block.Address < (ulong)block.Bytes.Length)
        return block.Bytes[(int)(address - block.Address)];
    }

    return null;
  }

  /// <inheritdoc/>
  public bool Equals(MachineSnapshot? other)
    => other is not null && Same(this.Registers, other.Registers) && Same(this.Flags, other.Flags) && this.Memory.SequenceEqual(other.Memory);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Registers.Count, this.Flags.Count, this.Memory.Length);

  private static bool Same<T>(ImmutableDictionary<string, T> left, ImmutableDictionary<string, T> right)
    => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && EqualityComparer<T>.Default.Equals(pair.Value, value));

}
