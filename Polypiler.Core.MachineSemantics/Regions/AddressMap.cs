using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Polypiler.Core.MachineSemantics.Regions;

/// <summary>What lies at a range of addresses.</summary>
public enum AddressKind {

  /// <summary>Read-write memory.</summary>
  Ram,

  /// <summary>Read-only memory.</summary>
  Rom,

  /// <summary>The stack.</summary>
  Stack,

  /// <summary>Memory-mapped device registers: every access is observable.</summary>
  Mmio,

  /// <summary>Nothing: an access faults.</summary>
  Unmapped

}

/// <summary>A named range of addresses, both ends inclusive.</summary>
/// <param name="Name">The name, unique in its map.</param>
/// <param name="Start">The first address.</param>
/// <param name="End">The last address.</param>
/// <param name="Kind">What lies there.</param>
/// <param name="Executable">Whether code runs from it.</param>
public sealed record AddressRange(string Name, ulong Start, ulong End, AddressKind Kind, bool Executable) {

  /// <summary>Whether an address lies in the range.</summary>
  /// <param name="address">The address.</param>
  /// <returns>Whether it does.</returns>
  public bool Contains(ulong address) => address >= this.Start && address <= this.End;

}

/// <summary>Finds the range an address lies in. A memory-map file loader implements it as well as <see cref="AddressMap"/>.</summary>
public interface IAddressMap {

  /// <summary>The range at an address, or <see langword="null"/> when no range covers it.</summary>
  /// <param name="address">The address.</param>
  /// <returns>The range.</returns>
  AddressRange? RangeAt(ulong address);

  /// <summary>Every range of the map, non-overlapping; the classifier walks them to find each range an access touches.</summary>
  ImmutableArray<AddressRange> Ranges { get; }

}

/// <summary>
/// An address map built from non-overlapping ranges. Building refuses overlapping ranges (<c>PLY4009</c>)
/// and a range that ends before it starts or leaves the address space (<c>PLY4011</c>).
/// </summary>
public sealed class AddressMap : IAddressMap {

  /// <summary><c>PLY4009</c>: two ranges overlap. Arguments: the two names, the first shared address.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Overlap = new("PLY4009", DiagnosticSeverity.Error, ExitCode.InvalidSource, "address ranges '{0}' and '{1}' overlap at {2}", "machine.regions.overlap");

  /// <summary><c>PLY4011</c>: a range that ends before it starts (argument: its name), or that leaves the address space (through <see cref="Diagnostic"/> with its own message).</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor BadRange = new("PLY4011", DiagnosticSeverity.Error, ExitCode.InvalidSource, "address range '{0}' ends before it starts", "machine.regions.range");

  private readonly ImmutableArray<AddressRange> _ranges;

  /// <summary>Builds a map.</summary>
  /// <param name="ranges">The ranges.</param>
  /// <param name="addressBits">The width of the address space, 1 to 64.</param>
  /// <param name="isa">The machine the map is for, named by <c>PLY4011</c>.</param>
  /// <exception cref="MachineException">The ranges break a rule; the first problem.</exception>
  public AddressMap(IEnumerable<AddressRange> ranges, int addressBits = 64, string isa = "unknown") {
    ArgumentNullException.ThrowIfNull(ranges);
    ArgumentNullException.ThrowIfNull(isa);
    var list = ranges.ToImmutableArray();
    var problems = Validate(list, addressBits, isa);
    if (!problems.IsEmpty)
      throw new MachineException(problems[0]);
    this._ranges = [.. list.OrderBy(range => range.Start)];
  }

  /// <summary>The ranges, ordered by start.</summary>
  public ImmutableArray<AddressRange> Ranges => this._ranges;

  /// <summary>The rules of a map, each broken one as a diagnostic.</summary>
  /// <param name="ranges">The ranges.</param>
  /// <param name="addressBits">The width of the address space.</param>
  /// <param name="isa">The machine, named by <c>PLY4011</c>.</param>
  /// <returns>What is wrong; empty when nothing is.</returns>
  public static ImmutableArray<Diagnostic> Validate(IReadOnlyList<AddressRange> ranges, int addressBits, string isa) {
    ArgumentNullException.ThrowIfNull(ranges);
    var problems = ImmutableArray.CreateBuilder<Diagnostic>();
    var top = addressBits >= 64 ? ulong.MaxValue : (1UL << addressBits) - 1;
    foreach (var range in ranges)
      if (range.End < range.Start)
        problems.Add(Diagnostic.Create(BadRange, null, range.Name));
      else if (range.End > top)
        problems.Add(BadRange.At($"address range '{range.Name}' exceeds the {addressBits}-bit address space of '{isa}'"));

    var ordered = ranges.Where(range => range.End >= range.Start).OrderBy(range => range.Start).ToList();
    for (var index = 1; index < ordered.Count; ++index)
      for (var before = 0; before < index; ++before)
        if (ordered[before].End >= ordered[index].Start)
          problems.Add(Diagnostic.Create(Overlap, null, ordered[before].Name, ordered[index].Name, MachineSyntax.Number(ordered[index].Start)));
    return problems.ToImmutable();
  }

  /// <inheritdoc/>
  public AddressRange? RangeAt(ulong address) => this._ranges.FirstOrDefault(range => range.Contains(address));

}
