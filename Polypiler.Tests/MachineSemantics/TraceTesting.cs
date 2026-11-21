using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Tracing;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.MachineSemantics;

/// <summary>Builders of traces and projections, and the paths of the trace fixtures.</summary>
internal static class TraceTesting {

  private static readonly MachineProfileCatalog _catalog = new([Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "machine")]);

  /// <summary><c>tests/fixtures/native/mach-trace</c>.</summary>
  public static string TraceFixtures { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "native", "mach-trace");

  /// <summary><c>spec/schemas/machine-vector.schema.json</c>.</summary>
  public static string SchemaFile { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "machine-vector.schema.json");

  /// <summary>A profile of <c>spec/machine</c>.</summary>
  public static IsaProfile Profile(string id) => _catalog.Find(id)!;

  /// <summary>A snapshot.</summary>
  public static MachineSnapshot State(IEnumerable<(string Name, ulong Value)>? registers = null, IEnumerable<(string Name, bool? Value)>? flags = null, IEnumerable<MemoryBytes>? memory = null)
    => new((registers ?? []).ToImmutableDictionary(pair => pair.Name, pair => pair.Value),
      (flags ?? []).ToImmutableDictionary(pair => pair.Name, pair => pair.Value),
      [.. memory ?? []]);

  /// <summary>A trace.</summary>
  public static ObservableTrace Trace(
    IEnumerable<(string Name, ulong Value)>? registers = null,
    IEnumerable<(string Name, bool? Value)>? flags = null,
    IEnumerable<MemoryBytes>? memory = null,
    IEnumerable<TraceEvent>? events = null,
    TraceOutcome outcome = TraceOutcome.Returned,
    long? cycles = null)
    => new(State(registers, flags, memory), [.. events ?? []], outcome, cycles);

  /// <summary>A projection.</summary>
  public static TraceProjection Observe(
    string[]? registers = null,
    string[]? flags = null,
    AddressSpan[]? memory = null,
    string[]? events = null,
    bool cycles = false,
    TraceMode mode = TraceMode.Semantic)
    => new([.. registers ?? []], [.. flags ?? []], [.. memory ?? []], [.. events ?? []], cycles, mode);

  /// <summary>Bytes at an address.</summary>
  public static MemoryBytes Bytes(ulong address, params byte[] bytes) => new(address, [.. bytes]);

  /// <summary>A memory write.</summary>
  public static MemoryWrite Write(ulong address, params byte[] bytes) => new(address, [.. bytes]);

  /// <summary>An 8-bit write to an I/O port.</summary>
  public static DeviceAccess Out(ulong port, ulong value) => new("io", port, 8, true, value);

}
