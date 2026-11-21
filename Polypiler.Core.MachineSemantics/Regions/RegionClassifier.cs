using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Polypiler.Core.MachineSemantics.Regions;

/// <summary>The class of one access of a function: its block, its index there, and the class.</summary>
/// <param name="Block">The block's label.</param>
/// <param name="Index">The index of the access in the block.</param>
/// <param name="Class">The class.</param>
public sealed record ClassifiedAccess(string Block, int Index, AccessClass Class);

/// <summary>
/// Assigns every MachineIR <c>Load</c> and <c>Store</c> a <see cref="MemoryClass"/> and its effect
/// channels from evidence only (PRD §17.2). The rules, the first that matches winning:
/// <list type="number">
/// <item>The address is a constant — an immediate, or a value a <c>Const</c> of the same block defines —
/// or a value with a proven range, and a map is given: each range of the map the accessed bytes touch
/// gives a class and a channel — <c>Mmio</c> <see cref="MemoryClass.MemoryMappedIO"/>, an executable range
/// written <see cref="MemoryClass.ExecutableMemory"/>, <c>Unmapped</c> or a byte in no range
/// <see cref="MemoryClass.UnknownMemory"/> (and <c>PLY4010</c> for a constant address), any other range
/// <see cref="MemoryClass.ProvenShared"/>. The access is on every channel and has the class highest in the
/// order <c>MemoryMappedIO</c>, <c>ExecutableMemory</c>, <c>UnknownMemory</c>, <c>ProvenShared</c>.</item>
/// <item>to 5. The provenance of the address value decides (<see cref="Provenance.Classify"/>): a private
/// allocation, a global allocation, a caller buffer of a declared contract, runtime-owned storage.</item>
/// <item value="6">Otherwise <see cref="MemoryClass.UnknownMemory"/>.</item>
/// </list>
/// Classification only adds restrictions: without a map and without provenance every access is unknown
/// memory, and no numeric value of an address alone makes it a device or a private variable.
/// </summary>
public static class RegionClassifier {

  /// <summary><c>PLY4010</c>: a constant address no declared region covers. Arguments: the instruction, where it is, the address.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Uncovered = new("PLY4010", DiagnosticSeverity.Warning, null, "'{0}' at {1} accesses constant address {2}, which no declared region covers; treated as unknown memory", "machine.regions.uncovered");

  /// <summary>Classifies one access.</summary>
  /// <param name="access">The <c>Load</c> or <c>Store</c>.</param>
  /// <param name="block">The block holding it, where a <c>Const</c> may define its address.</param>
  /// <param name="map">The address map, or <see langword="null"/> when none is declared.</param>
  /// <param name="facts">The provenance facts of the function.</param>
  /// <param name="sink">Receives <c>PLY4010</c>, or <see langword="null"/>.</param>
  /// <param name="function">The function's name, without <c>@</c>: the key of the facts and part of where <c>PLY4010</c> points.</param>
  /// <returns>The class.</returns>
  /// <exception cref="ArgumentException">The instruction is no memory access, accesses no whole number of bytes, or is not in the block.</exception>
  public static AccessClass Classify(MachineInstruction access, MachineBlock block, IAddressMap? map, IProvenanceFacts facts, IDiagnosticSink? sink = null, string function = "") {
    ArgumentNullException.ThrowIfNull(access);
    ArgumentNullException.ThrowIfNull(block);
    var index = block.Instructions.IndexOf(access);
    if (index < 0)
      throw new ArgumentException($"'{access}' is not in block '{block.Label}'", nameof(block));
    return Classify(block, index, map, facts, sink, function);
  }

  /// <summary>Classifies every access of a function, in block and instruction order. The text form of MachineIR is not changed.</summary>
  /// <param name="function">The function.</param>
  /// <param name="map">The address map, or <see langword="null"/>.</param>
  /// <param name="facts">The provenance facts.</param>
  /// <param name="sink">Receives <c>PLY4010</c>, or <see langword="null"/>.</param>
  /// <returns>The class of every <c>Load</c> and <c>Store</c>.</returns>
  public static ImmutableArray<ClassifiedAccess> Annotate(MachineFunction function, IAddressMap? map, IProvenanceFacts facts, IDiagnosticSink? sink = null) {
    ArgumentNullException.ThrowIfNull(function);
    var classes = ImmutableArray.CreateBuilder<ClassifiedAccess>();
    foreach (var block in function.Blocks)
      for (var index = 0; index < block.Instructions.Length; ++index)
        if (block.Instructions[index].Opcode is MachineOpcode.Load or MachineOpcode.Store)
          classes.Add(new(block.Label, index, Classify(block, index, map, facts, sink, function.Name)));
    return classes.ToImmutable();
  }

  private static AccessClass Classify(MachineBlock block, int index, IAddressMap? map, IProvenanceFacts facts, IDiagnosticSink? sink, string function) {
    ArgumentNullException.ThrowIfNull(facts);
    ArgumentNullException.ThrowIfNull(function);
    var access = block.Instructions[index];
    if (access.Opcode is not (MachineOpcode.Load or MachineOpcode.Store))
      throw new ArgumentException($"'{access}' is no memory access", nameof(access));
    if (access.Type.Bits == 0 || access.Type.Bits % 8 != 0)
      throw new ArgumentException($"'{access}' accesses no whole number of bytes", nameof(access));
    if (access.Operands.IsEmpty || access.Operands[0] is not (MachineValue or MachineImmediate))
      throw new ArgumentException($"'{access}' has no address", nameof(access));
    var width = (ulong)(access.Type.Bits / 8);
    var address = access.Operands[0];
    var constant = Constant(address, block, index);
    var value = (address as MachineValue)?.Name;

    var span = constant is { } at ? new ProvenRange(at, at) : facts.RangeOf(function, ((MachineValue)address).Name);
    if (map is not null && span is not null) {
      Action<ulong>? warn = constant is null ? null
        : uncovered => sink?.Report(Diagnostic.Create(Uncovered, null, (access with { Origin = null }).ToString(), Where(access, block, function), MachineSyntax.Number(uncovered)));
      return ByMap(span, width, access.Opcode == MachineOpcode.Store, map, warn);
    }

    if (value is not null && facts.ProvenanceOf(function, value) is { } provenance)
      return provenance.Classify();
    return new(MemoryClass.UnknownMemory, [MemoryChannels.Unknown], constant is { } number ? $"constant address {MachineSyntax.Number(number)} and no address map" : "no provenance");
  }

  private static ulong? Constant(MachineOperand address, MachineBlock block, int index) {
    if (address is MachineImmediate immediate)
      return immediate.Value;
    var name = ((MachineValue)address).Name;
    for (var before = 0; before < index; ++before)
      if (block.Instructions[before] is { Opcode: MachineOpcode.Const, Operands: [MachineImmediate definition] } constant && constant.Result == name)
        return definition.Value;
    return null;
  }

  private static string Where(MachineInstruction access, MachineBlock block, string function)
    => access.Origin is { } origin ? MachineSyntax.Number(origin.Address) : function.Length == 0 ? block.Label : $"@{function}:{block.Label}";

  private static int Rank(MemoryClass memoryClass) => memoryClass switch {
    MemoryClass.MemoryMappedIO => 3,
    MemoryClass.ExecutableMemory => 2,
    MemoryClass.UnknownMemory => 1,
    _ => 0
  };

  private static AccessClass ByMap(ProvenRange span, ulong width, bool write, IAddressMap map, Action<ulong>? warn) {
    var low = span.Low;
    var wraps = span.High > ulong.MaxValue - (width - 1);
    var high = wraps ? ulong.MaxValue : span.High + (width - 1);

    var parts = new List<(MemoryClass Class, string Channel)>();
    var names = new List<string>();
    ulong? uncovered = null;
    void Gap(ulong at) {
      parts.Add((MemoryClass.UnknownMemory, MemoryChannels.Unknown));
      uncovered ??= at;
    }

    var cursor = low;
    var done = false;
    foreach (var range in map.Ranges.Where(range => range.End >= low && range.Start <= high).OrderBy(range => range.Start)) {
      if (range.Start > cursor)
        Gap(cursor);
      names.Add(range.Name);
      if (range.Kind == AddressKind.Unmapped)
        Gap(Math.Max(range.Start, low));
      else
        parts.Add(range.Kind == AddressKind.Mmio ? (MemoryClass.MemoryMappedIO, MemoryChannels.Device(range.Name))
          : range.Executable && write ? (MemoryClass.ExecutableMemory, MemoryChannels.ExecutableWrite(range.Name))
          : (MemoryClass.ProvenShared, MemoryChannels.Memory(range.Name)));
      if (range.End >= high) {
        done = true;
        break;
      }

      cursor = range.End + 1;
    }

    if (!done)
      Gap(cursor);
    if (wraps)
      Gap(0);

    var memoryClass = parts.Select(part => part.Class).MaxBy(Rank);
    var evidence = new List<string>();
    if (names.Count > 0)
      evidence.Add((names.Count == 1 ? "map range " : "map ranges ") + string.Join(", ", names.Select(name => $"'{name}'")));
    if (uncovered is { } first) {
      evidence.Add($"no mapped region at {MachineSyntax.Number(first)}");
      warn?.Invoke(first);
    }

    return new(memoryClass, [.. parts.Select(part => part.Channel).Distinct()], string.Join("; ", evidence));
  }

}
