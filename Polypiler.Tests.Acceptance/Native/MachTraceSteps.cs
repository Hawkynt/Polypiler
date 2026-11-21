using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Tracing;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Native;

/// <summary>
/// The steps of <c>tests/features/native/mach-trace.feature</c>: hand-built trace pairs compared in
/// process by <see cref="TraceComparer"/>, and a machine vector file loaded against the
/// <c>test-m32</c> profile of <c>spec/machine</c>.
/// </summary>
[Binding, Scope(Feature = "Observable-state trace and comparison")]
public sealed class MachTraceSteps {

  private ObservableTrace? _expected;
  private ObservableTrace? _actual;
  private TraceProjection? _projection;
  private TraceDiff? _diff;
  private string? _file;
  private Diagnostic? _refusal;

  private TraceDiff Diff => this._diff ?? throw new AssertionException("nothing was compared");

  private static ulong Hex(string text) => ulong.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

  private static ObservableTrace Trace(ImmutableDictionary<string, ulong>? registers = null, ImmutableDictionary<string, bool?>? flags = null, ImmutableArray<MemoryBytes> memory = default, ImmutableArray<TraceEvent> events = default)
    => new(new(registers ?? ImmutableDictionary<string, ulong>.Empty, flags ?? ImmutableDictionary<string, bool?>.Empty, memory.IsDefault ? [] : memory), events.IsDefault ? [] : events, TraceOutcome.Returned);

  private static TraceProjection Observe(ImmutableArray<string> registers = default, ImmutableArray<string> flags = default, ImmutableArray<AddressSpan> memory = default, ImmutableArray<string> events = default, TraceMode mode = TraceMode.Semantic)
    => new(registers.IsDefault ? [] : registers, flags.IsDefault ? [] : flags, memory.IsDefault ? [] : memory, events.IsDefault ? [] : events, false, mode);

  private static ObservableTrace Registers(string first, string firstValue, string second, string secondValue)
    => Trace(ImmutableDictionary<string, ulong>.Empty.Add(first, Hex(firstValue)).Add(second, Hex(secondValue)));

  /// <summary>The expected side with two registers.</summary>
  [Given(@"^an expected trace with (\w+) = (0x[0-9A-F]+) and (\w+) = (0x[0-9A-F]+)$")]
  public void ExpectedRegisters(string first, string firstValue, string second, string secondValue) => this._expected = Registers(first, firstValue, second, secondValue);

  /// <summary>The actual side with two registers.</summary>
  [Given(@"^an actual trace with (\w+) = (0x[0-9A-F]+) and (\w+) = (0x[0-9A-F]+)$")]
  public void ActualRegisters(string first, string firstValue, string second, string secondValue) => this._actual = Registers(first, firstValue, second, secondValue);

  /// <summary>A projection of one register.</summary>
  [Given(@"^a projection observing only the register (\w+)$")]
  public void OnlyRegister(string register) => this._projection = Observe(registers: [register]);

  /// <summary>Two port writes on the expected side.</summary>
  [Given(@"^an expected trace with the events device-write port (0x[0-9A-F]+) = (0x[0-9A-F]+) then device-write port (0x[0-9A-F]+) = (0x[0-9A-F]+)$")]
  public void ExpectedPortWrites(string firstPort, string firstValue, string secondPort, string secondValue)
    => this._expected = Trace(events: [new DeviceAccess("io", Hex(firstPort), 8, true, Hex(firstValue)), new DeviceAccess("io", Hex(secondPort), 8, true, Hex(secondValue))]);

  /// <summary>The same events, reversed, on the actual side.</summary>
  [Given(@"^an actual trace with the same two events in the opposite order$")]
  public void ReversedEvents() => this._actual = this._expected! with { Events = [.. Enumerable.Reverse(this._expected!.Events)] };

  /// <summary>A projection of one event kind.</summary>
  [Given("^a projection observing the event kind " + StepText.Quoted + "$")]
  public void EventKind(string kind) => this._projection = Observe(events: [kind]);

  /// <summary>Two memory writes in program order against the same writes reversed, with the same final bytes.</summary>
  [Given(@"^an expected trace whose final bytes at (0x[0-9A-F]+) are ([0-9A-F]{2}) ([0-9A-F]{2}) and an actual trace that wrote (0x[0-9A-F]+) before (0x[0-9A-F]+) with the same final bytes$")]
  public void ReorderedWrites(string address, string low, string high, string first, string second) {
    var start = Hex(address);
    var bytes = Convert.FromHexString(low + high);
    Assert.That((Hex(first), Hex(second)), Is.EqualTo((start + 1, start)));
    ImmutableArray<MemoryBytes> final = [new(start, [.. bytes])];
    this._expected = Trace(memory: final, events: [new MemoryWrite(start, [bytes[0]]), new MemoryWrite(start + 1, [bytes[1]])]);
    this._actual = Trace(memory: final, events: [new MemoryWrite(start + 1, [bytes[1]]), new MemoryWrite(start, [bytes[0]])]);
  }

  /// <summary>A projection of a memory range in a mode.</summary>
  [Given(@"^a projection observing memory (0x[0-9A-F]+) length (\d+) in mode (Semantic|Raw)$")]
  public void MemoryRange(string address, int length, TraceMode mode) => this._projection = Observe(memory: [new(Hex(address), length)], mode: mode);

  /// <summary>A projection of an event kind in a mode.</summary>
  [Given("^a projection observing " + StepText.Quoted + @" events in mode (Semantic|Raw)$")]
  public void EventKindInMode(string kind, TraceMode mode) => this._projection = Observe(events: [kind], mode: mode);

  /// <summary>A defined expected flag and an undefined actual one.</summary>
  [Given(@"^an expected trace with flag (\w+) = ([01]) and an actual trace with flag \w+ undefined$")]
  public void UndefinedFlag(string flag, string value) {
    this._expected = Trace(flags: ImmutableDictionary<string, bool?>.Empty.Add(flag, value == "1"));
    this._actual = Trace(flags: ImmutableDictionary<string, bool?>.Empty.Add(flag, null));
  }

  /// <summary>A projection of one flag.</summary>
  [Given(@"^a projection observing the flag (\w+)$")]
  public void OneFlag(string flag) => this._projection = Observe(flags: [flag]);

  /// <summary>A vector file that observes a register by name.</summary>
  [Given("^a machine vector file for ISA " + StepText.Quoted + " whose observe.registers contains " + StepText.Quoted + "$")]
  public void VectorFile(string isa, string register) {
    this._file = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mvec.json");
    File.WriteAllText(this._file, $$"""
      {"schemaVersion": 1, "fixture": "mach-trace.feature", "isa": "{{isa}}", "provenance": {"kind": "published", "producer": "hand", "source": "feature"},
       "observe": {"registers": ["{{register}}"], "flags": [], "memory": [], "events": [], "cycles": false, "mode": "semantic"}, "vectors": []}
      """);
  }

  /// <summary>Compares the two traces.</summary>
  [When("^TraceComparer.Compare runs$")]
  public void Compare() => this._diff = TraceComparer.Compare(this._expected!, this._actual!, this._projection!);

  /// <summary>Loads the vector file against its profile.</summary>
  [When("^MachineVectorFile.Load runs$")]
  public void Load() {
    var profile = new MachineProfileCatalog([Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "machine")]).Find("test-m32")!;
    try {
      MachineVectorFile.Load(this._file!, profile);
    } catch (MachineException exception) {
      this._refusal = exception.Diagnostic;
    } finally {
      File.Delete(this._file!);
    }
  }

  /// <summary>No difference.</summary>
  [Then("^the diff is Equal$")]
  public void IsEqual() => Assert.That(this.Diff, Is.EqualTo(TraceDiff.Same));

  /// <summary>A difference at a place.</summary>
  [Then("^the diff reports " + StepText.Quoted + "$")]
  public void Reports(string what) => Assert.That(this.Diff.What, Is.EqualTo(what));

  /// <summary>A difference between two events at their addresses.</summary>
  [Then("^the diff reports " + StepText.Quoted + @" with expected address (0x[0-9A-F]+) and actual address (0x[0-9A-F]+)$")]
  public void ReportsAddresses(string what, string expected, string actual) {
    Assert.That(this.Diff.What, Is.EqualTo(what));
    Assert.That(this.Diff.Expected, Does.Contain($" {expected} "));
    Assert.That(this.Diff.Actual, Does.Contain($" {actual} "));
  }

  /// <summary>A difference with both sides' text.</summary>
  [Then("^the diff reports " + StepText.Quoted + @" with expected (\w+) and actual " + StepText.Quoted + "$")]
  public void ReportsValues(string what, string expected, string actual)
    => Assert.That(this.Diff, Is.EqualTo(TraceDiff.Differs(what, expected, actual)));

  /// <summary>The load failed with a code naming two things.</summary>
  [Then("^it fails with (PLY\\d{4}) naming " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Fails(string code, string first, string second) {
    Assert.That(this._refusal, Is.Not.Null);
    Assert.That(this._refusal!.Code, Is.EqualTo(code));
    Assert.That(this._refusal.Message, Does.Contain($"'{first}'").And.Contain($"'{second}'"));
  }

}
