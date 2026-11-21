using System;
using System.Collections.Immutable;

namespace Polypiler.Core.MachineSemantics.Regions;

/// <summary>A proven range of values of an address, both ends inclusive.</summary>
public sealed record ProvenRange {

  /// <summary>Creates a range.</summary>
  /// <param name="low">The lowest address.</param>
  /// <param name="high">The highest address.</param>
  /// <exception cref="ArgumentException"><paramref name="high"/> is below <paramref name="low"/>.</exception>
  public ProvenRange(ulong low, ulong high) {
    if (high < low)
      throw new ArgumentException($"a proven range ends at {MachineSyntax.Number(high)}, before its start {MachineSyntax.Number(low)}", nameof(high));
    this.Low = low;
    this.High = high;
  }

  /// <summary>The lowest address.</summary>
  public ulong Low { get; }

  /// <summary>The highest address.</summary>
  public ulong High { get; }

}

/// <summary>Where the memory an address points into comes from. Each kind decides the class of an access through it.</summary>
public abstract record Provenance {

  private protected Provenance() {
  }

  /// <summary>The class an access through an address of this provenance gets.</summary>
  /// <returns>The class, its channel and the evidence.</returns>
  public abstract AccessClass Classify();

}

/// <summary>An allocation private to the function, such as a stack frame buffer no one else sees.</summary>
/// <param name="Allocation">The allocation's name.</param>
public sealed record PrivateAllocation(string Allocation) : Provenance {

  /// <inheritdoc/>
  public override AccessClass Classify() => new(MemoryClass.ProvenLocal, [MemoryChannels.Memory(this.Allocation)], $"private allocation '{this.Allocation}'");

}

/// <summary>A global or module-level allocation.</summary>
/// <param name="Allocation">The allocation's name.</param>
public sealed record GlobalAllocation(string Allocation) : Provenance {

  /// <inheritdoc/>
  public override AccessClass Classify() => new(MemoryClass.ProvenShared, [MemoryChannels.Memory(this.Allocation)], $"global allocation '{this.Allocation}'");

}

/// <summary>A buffer the caller supplies under a declared contract.</summary>
/// <param name="Name">The buffer's name.</param>
/// <param name="Contract">The contract, such as <c>buffer(len=16)</c>.</param>
public sealed record CallerBuffer(string Name, string Contract) : Provenance {

  /// <inheritdoc/>
  public override AccessClass Classify() => new(MemoryClass.ExternalMemory, [MemoryChannels.External(this.Name)], $"caller buffer '{this.Name}' of contract '{this.Contract}'");

}

/// <summary>Storage a runtime owns, declared by a runtime dependency.</summary>
/// <param name="Runtime">The runtime.</param>
public sealed record RuntimeStorage(string Runtime) : Provenance {

  /// <inheritdoc/>
  public override AccessClass Classify() => new(MemoryClass.RuntimeState, [MemoryChannels.Runtime(this.Runtime)], $"storage of runtime '{this.Runtime}'");

}

/// <summary>
/// What is proven about the addresses of a function: for a MachineIR value, the range of its values and
/// the provenance of the memory it points into. Pointer-provenance analysis implements it.
/// </summary>
public interface IProvenanceFacts {

  /// <summary>The proven range of a value, or <see langword="null"/> when none is proven.</summary>
  /// <param name="function">The function, without <c>@</c>.</param>
  /// <param name="value">The value, without <c>%</c>.</param>
  /// <returns>The range.</returns>
  ProvenRange? RangeOf(string function, string value);

  /// <summary>The provenance of a value, or <see langword="null"/> when it has none.</summary>
  /// <param name="function">The function, without <c>@</c>.</param>
  /// <param name="value">The value, without <c>%</c>.</param>
  /// <returns>The provenance.</returns>
  Provenance? ProvenanceOf(string function, string value);

}

/// <summary>No facts at all: every value has neither range nor provenance.</summary>
public sealed class NoProvenanceFacts : IProvenanceFacts {

  private NoProvenanceFacts() {
  }

  /// <summary>The one instance.</summary>
  public static NoProvenanceFacts Instance { get; } = new();

  /// <inheritdoc/>
  public ProvenRange? RangeOf(string function, string value) => null;

  /// <inheritdoc/>
  public Provenance? ProvenanceOf(string function, string value) => null;

}

/// <summary>Facts given one by one, for declared contracts and for tests.</summary>
public sealed class ProvenanceFactTable : IProvenanceFacts {

  private readonly ImmutableDictionary<(string Function, string Value), ProvenRange> _ranges;
  private readonly ImmutableDictionary<(string Function, string Value), Provenance> _provenances;

  /// <summary>Creates an empty table.</summary>
  public ProvenanceFactTable() : this(ImmutableDictionary<(string, string), ProvenRange>.Empty, ImmutableDictionary<(string, string), Provenance>.Empty) {
  }

  private ProvenanceFactTable(ImmutableDictionary<(string, string), ProvenRange> ranges, ImmutableDictionary<(string, string), Provenance> provenances) {
    this._ranges = ranges;
    this._provenances = provenances;
  }

  /// <summary>The table with a proven range for a value.</summary>
  /// <param name="function">The function, without <c>@</c>.</param>
  /// <param name="value">The value, without <c>%</c>.</param>
  /// <param name="range">The range.</param>
  /// <returns>The new table.</returns>
  public ProvenanceFactTable WithRange(string function, string value, ProvenRange range) {
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(value);
    ArgumentNullException.ThrowIfNull(range);
    return new(this._ranges.SetItem((function, value), range), this._provenances);
  }

  /// <summary>The table with a provenance for a value.</summary>
  /// <param name="function">The function, without <c>@</c>.</param>
  /// <param name="value">The value, without <c>%</c>.</param>
  /// <param name="provenance">The provenance.</param>
  /// <returns>The new table.</returns>
  public ProvenanceFactTable WithProvenance(string function, string value, Provenance provenance) {
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(value);
    ArgumentNullException.ThrowIfNull(provenance);
    return new(this._ranges, this._provenances.SetItem((function, value), provenance));
  }

  /// <inheritdoc/>
  public ProvenRange? RangeOf(string function, string value) => this._ranges.GetValueOrDefault((function, value));

  /// <inheritdoc/>
  public Provenance? ProvenanceOf(string function, string value) => this._provenances.GetValueOrDefault((function, value));

}
