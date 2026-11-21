using System;
using System.Collections.Generic;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.MachineSemantics.Tracing;
using static Polypiler.Tests.MachineSemantics.TraceTesting;

namespace Polypiler.Tests.MachineSemantics;

/// <summary>
/// <see cref="TraceComparer"/> on hand-built trace pairs with one deliberate difference at a known
/// place, the order of the comparison, both modes, and the trace records' own equality.
/// </summary>
[TestFixture]
public sealed class TraceComparerTests {

  private static readonly TraceProjection _everything = Observe(["r0", "r1"], ["c"], [new(0x1000, 2)], [.. TraceEvent.Kinds], true, TraceMode.Raw);

  private static ObservableTrace Full(ulong r0 = 5, bool? c = true, byte last = 2, ulong port = 0x3C8, TraceOutcome outcome = TraceOutcome.Returned, long? cycles = 12)
    => Trace([("r0", r0), ("r1", 1)], [("c", c)], [Bytes(0x1000, 1, last)], [Out(port, 0x10), Write(0x1000, 1, last), new Return(r0)], outcome, cycles);

  /// <summary>Test design 1: identical traces.</summary>
  [Test]
  public void IdenticalTraces_AreEqual()
    => Assert.That(TraceComparer.Compare(Full(), Full(), _everything), Is.EqualTo(TraceDiff.Same));

  /// <summary>Test design 2: a register outside the projection differs.</summary>
  [Test]
  public void ADifferenceOutsideTheProjection_IsEqual()
    => Assert.That(TraceComparer.Compare(Trace([("r0", 5), ("r1", 1)]), Trace([("r0", 5), ("r1", 0xFF)]), Observe(["r0"])).Equal, Is.True);

  private static IEnumerable<TestCaseData> SingleDifferences() {
    yield return new TestCaseData(Full(outcome: TraceOutcome.Trapped), "outcome", "returned", "trapped").SetName("Differs_Outcome");
    yield return new TestCaseData(Full(r0: 6), "register r0", "0x5", "0x6").SetName("Differs_Register");
    yield return new TestCaseData(Full(c: false), "flag c", "1", "0").SetName("Differs_Flag");
    yield return new TestCaseData(Full(last: 3), "memory 0x1000+1", "0x02", "0x03").SetName("Differs_MemoryByte");
    yield return new TestCaseData(Full(port: 0x3C9), "event 0", "device-write io 0x3C8 u8 = 0x10", "device-write io 0x3C9 u8 = 0x10").SetName("Differs_Event");
    yield return new TestCaseData(Full(cycles: 14), "cycles", "12", "14").SetName("Differs_Cycles");
  }

  /// <summary>Test design 3: each compared field differs alone.</summary>
  [TestCaseSource(nameof(SingleDifferences))]
  public void EachField_DiffersAlone(ObservableTrace actual, string what, string expected, string actualText)
    => Assert.That(TraceComparer.Compare(Full(), actual, _everything), Is.EqualTo(new TraceDiff(false, what, expected, actualText, what == "event 0" ? 0 : null)));

  /// <summary>The register comes before the flag, the flag before memory: only the first difference is reported.</summary>
  [Test]
  public void OnlyTheFirstDifference_IsReported()
    => Assert.That(TraceComparer.Compare(Full(), Full(r0: 6, c: false, last: 9, port: 1, cycles: 1), _everything).What, Is.EqualTo("register r0"));

  /// <summary>A register the projection observes but one side lacks is <c>none</c> there.</summary>
  [Test]
  public void AMissingRegister_IsNone()
    => Assert.That(TraceComparer.Compare(Trace([("r0", 1)]), Trace(), Observe(["r0"])), Is.EqualTo(TraceDiff.Differs("register r0", "0x1", "none")));

  private static IEnumerable<TestCaseData> EventFieldPairs() {
    yield return new TestCaseData(new DeviceAccess("io", 0x60, 8, false, 1), new DeviceAccess("mmio", 0x60, 8, false, 1)).SetName("Device_Space");
    yield return new TestCaseData(new DeviceAccess("io", 0x60, 8, false, 1), new DeviceAccess("io", 0x61, 8, false, 1)).SetName("Device_Address");
    yield return new TestCaseData(new DeviceAccess("io", 0x60, 8, false, 1), new DeviceAccess("io", 0x60, 16, false, 1)).SetName("Device_Width");
    yield return new TestCaseData(new DeviceAccess("io", 0x60, 8, false, 1), new DeviceAccess("io", 0x60, 8, true, 1)).SetName("Device_Direction");
    yield return new TestCaseData(new DeviceAccess("io", 0x60, 8, false, 1), new DeviceAccess("io", 0x60, 8, false, 2)).SetName("Device_Value");
    yield return new TestCaseData(new ExternalCall("f", "c", [1], 2), new ExternalCall("g", "c", [1], 2)).SetName("External_Symbol");
    yield return new TestCaseData(new ExternalCall("f", "c", [1], 2), new ExternalCall("f", null, [1], 2)).SetName("External_Abi");
    yield return new TestCaseData(new ExternalCall("f", "c", [1], 2), new ExternalCall("f", "c", [1, 2], 2)).SetName("External_Arguments");
    yield return new TestCaseData(new ExternalCall("f", "c", [1], 2), new ExternalCall("f", "c", [1], null)).SetName("External_Result");
    yield return new TestCaseData(new Trap("divide-error", 0, 0x10), new Trap("page-fault", 0, 0x10)).SetName("Trap_Kind");
    yield return new TestCaseData(new Trap("divide-error", 0, 0x10), new Trap("divide-error", null, 0x10)).SetName("Trap_Vector");
    yield return new TestCaseData(new Trap("divide-error", 0, 0x10), new Trap("divide-error", 0, null)).SetName("Trap_At");
    yield return new TestCaseData(Write(0x1000, 1), Write(0x1001, 1)).SetName("MemoryWrite_Address");
    yield return new TestCaseData(Write(0x1000, 1), Write(0x1000, 2)).SetName("MemoryWrite_Bytes");
    yield return new TestCaseData(new Return(1), new Return(null)).SetName("Return_Value");
    yield return new TestCaseData(new Return(1), Out(0x60, 1)).SetName("Event_Kind");
  }

  /// <summary>Test design 4: every event kind is compared field by field.</summary>
  [TestCaseSource(nameof(EventFieldPairs))]
  public void EachEventField_IsCompared(TraceEvent expected, TraceEvent actual) {
    var diff = TraceComparer.Compare(Trace(events: [expected]), Trace(events: [actual]), _everything);
    Assert.That(diff, Is.EqualTo(TraceDiff.Differs("event 0", expected.Text, actual.Text, 0)));
    Assert.That(TraceComparer.Compare(Trace(events: [expected]), Trace(events: [expected with { }]), _everything).Equal, Is.True);
  }

  /// <summary>Test design 5: reordered memory writes with the same final bytes, in both modes.</summary>
  [TestCase(TraceMode.Semantic, true)]
  [TestCase(TraceMode.Raw, false)]
  public void ReorderedMemoryWrites_DependOnTheMode(TraceMode mode, bool equal) {
    var expected = Trace(memory: [Bytes(0x1000, 1, 2)], events: [Write(0x1000, 1), Write(0x1001, 2)]);
    var actual = Trace(memory: [Bytes(0x1000, 1, 2)], events: [Write(0x1001, 2), Write(0x1000, 1)]);
    var diff = TraceComparer.Compare(expected, actual, Observe(memory: [new(0x1000, 2)], events: [TraceEvent.MemoryWriteKind], mode: mode));
    Assert.That(diff.Equal, Is.EqualTo(equal));
    Assert.That(diff.What, Is.EqualTo(equal ? null : "event 0"));
  }

  /// <summary>Semantic mode compares device, external and trap events as one interleaved sequence.</summary>
  [Test]
  public void SemanticMode_InterleavesTheOtherKinds() {
    var expected = Trace(events: [Out(0x60, 1), new ExternalCall("f", null, [], null), Write(0x1000, 1)]);
    var actual = Trace(events: [Write(0x1000, 9), new ExternalCall("f", null, [], null), Out(0x60, 1)]);
    Assert.That(TraceComparer.Compare(expected, actual, Observe(events: [.. TraceEvent.Kinds])).What, Is.EqualTo("event 0"));
  }

  /// <summary>Test design 6: an expected undefined flag matches 0, 1 and undefined.</summary>
  [TestCase(false)]
  [TestCase(true)]
  [TestCase(null)]
  public void AnExpectedUndefinedFlag_MatchesAnything(bool? actual)
    => Assert.That(TraceComparer.Compare(Trace(flags: [("c", null)]), Trace(flags: [("c", actual)]), Observe(flags: ["c"])).Equal, Is.True);

  /// <summary>Test design 7: no events on either side.</summary>
  [Test]
  public void EmptyEventLists_AreEqual()
    => Assert.That(TraceComparer.Compare(Trace(), Trace(), Observe(events: [.. TraceEvent.Kinds], mode: TraceMode.Raw)).Equal, Is.True);

  /// <summary>Test design 8: one event more or one fewer.</summary>
  [TestCase(2, 3)]
  [TestCase(3, 2)]
  public void AnEventMoreOrFewer_IsReportedAtTheEnd(int expectedCount, int actualCount) {
    static IEnumerable<TraceEvent> Events(int count) {
      for (var index = 0; index < count; ++index)
        yield return Out(0x60, (ulong)index);
    }

    var diff = TraceComparer.Compare(Trace(events: Events(expectedCount)), Trace(events: Events(actualCount)), Observe(events: ["device"]));
    Assert.That(diff.What, Is.EqualTo("event 2"));
    Assert.That(diff.EventIndex, Is.EqualTo(2));
    Assert.That(expectedCount < actualCount ? diff.Expected : diff.Actual, Is.EqualTo("none"));
  }

  /// <summary>Test design 9: a range of length 0 compares nothing, of length 1 one byte.</summary>
  [TestCase(0, true)]
  [TestCase(1, false)]
  public void RangeLength_BoundsTheComparison(int length, bool equal) {
    var diff = TraceComparer.Compare(Trace(memory: [Bytes(0x1000, 1, 2)]), Trace(memory: [Bytes(0x1000, 9, 2)]), Observe(memory: [new(0x1000, length)]));
    Assert.That(diff.Equal, Is.EqualTo(equal));
    Assert.That(TraceComparer.Compare(Trace(memory: [Bytes(0x1000, 1, 2)]), Trace(memory: [Bytes(0x1000, 1, 9)]), Observe(memory: [new(0x1000, length)])).Equal, Is.True);
  }

  /// <summary>Test design 10: the last byte of a range differs; a byte one side lacks is <c>none</c>.</summary>
  [Test]
  public void TheLastByteOfARange_IsCompared() {
    var projection = Observe(memory: [new(0x1000, 4)]);
    Assert.That(TraceComparer.Compare(Trace(memory: [Bytes(0x1000, 1, 2, 3, 4)]), Trace(memory: [Bytes(0x1000, 1, 2, 3, 5)]), projection),
      Is.EqualTo(TraceDiff.Differs("memory 0x1000+3", "0x04", "0x05")));
    Assert.That(TraceComparer.Compare(Trace(memory: [Bytes(0x1000, 1, 2, 3, 4)]), Trace(memory: [Bytes(0x1000, 1, 2, 3)]), projection),
      Is.EqualTo(TraceDiff.Differs("memory 0x1000+3", "0x04", "none")));
  }

  /// <summary>Test design 13: an undefined actual flag where a defined one is expected.</summary>
  [Test]
  public void AnUndefinedActualFlag_IsADifference()
    => Assert.That(TraceComparer.Compare(Trace(flags: [("c", true)]), Trace(flags: [("c", null)]), Observe(flags: ["c"])),
      Is.EqualTo(TraceDiff.Differs("flag c", "1", "undefined")));

  /// <summary>Test design 14: a reference with device events the projection does not observe.</summary>
  [Test]
  public void AReferenceWithUnobservedDeviceEvents_IsPly4033() {
    var reference = Trace(events: [Out(0x60, 1), Out(0x61, 2), new Trap("x", null, null), Write(0x1000, 1), new Return(null)]);
    var problems = TraceComparer.CheckReference("mach-trace.device", "v", reference, Observe(events: ["trap"]));
    Assert.That(problems, Has.Length.EqualTo(1));
    Assert.That(problems[0].Code, Is.EqualTo("PLY4033"));
    Assert.That(problems[0].Message, Is.EqualTo("'mach-trace.device' vector 'v': the reference produced 2 event(s) of kind 'device' but the projection does not observe that kind"));
    Assert.That(TraceComparer.CheckReference("f", "v", reference, Observe(events: ["device", "trap"])), Is.Empty);
  }

  /// <summary>Test design 15: cycles not observed, observed with an actual <c>null</c>, observed without an expectation.</summary>
  [Test]
  public void Cycles_AreComparedOnlyWhenObserved() {
    Assert.That(TraceComparer.Compare(Trace(cycles: 12), Trace(cycles: 14), Observe(cycles: false)).Equal, Is.True);
    Assert.That(TraceComparer.Compare(Trace(cycles: 12), Trace(), Observe(cycles: true)), Is.EqualTo(TraceDiff.Differs("cycles", "12", "none")));
    Assert.That(TraceComparer.Compare(Trace(), Trace(cycles: 14), Observe(cycles: true)).Equal, Is.True);
    Assert.That(TraceComparer.Compare(Trace(cycles: 12), Trace(cycles: 12), Observe(cycles: true)).Equal, Is.True);
  }

  /// <summary>Test design 16: <c>BudgetExceeded</c> on both sides and on one side.</summary>
  [Test]
  public void BudgetExceeded_IsAnOutcome() {
    Assert.That(TraceComparer.Compare(Trace(outcome: TraceOutcome.BudgetExceeded), Trace(outcome: TraceOutcome.BudgetExceeded), _everything).Equal, Is.True);
    Assert.That(TraceComparer.Compare(Trace(outcome: TraceOutcome.BudgetExceeded), Trace(), _everything), Is.EqualTo(TraceDiff.Differs("outcome", "budget-exceeded", "returned")));
  }

  [TestCase(TraceOutcome.Returned, "returned")]
  [TestCase(TraceOutcome.Trapped, "trapped")]
  [TestCase(TraceOutcome.Aborted, "aborted")]
  [TestCase(TraceOutcome.BudgetExceeded, "budget-exceeded")]
  public void Outcomes_AreSpelledAsInTheFile(TraceOutcome outcome, string name) => Assert.That(TraceComparer.Name(outcome), Is.EqualTo(name));

  /// <summary>PLY4031 from a difference; an equal diff has nothing to report.</summary>
  [Test]
  public void ADifference_BecomesPly4031() {
    var diagnostic = TraceComparer.ToDiagnostic("mach-trace.return", "add", TraceDiff.Differs("register r0", "0x5", "0x6"));
    Assert.That(diagnostic.Code, Is.EqualTo("PLY4031"));
    Assert.That(diagnostic.ExitCode, Is.EqualTo(ExitCode.SemanticMismatch));
    Assert.That(diagnostic.Message, Is.EqualTo("'mach-trace.return' vector 'add': register r0 expected 0x5, actual 0x6"));
    Assert.That(() => TraceComparer.ToDiagnostic("f", "v", TraceDiff.Same), Throws.ArgumentException);
  }

  [Test]
  public void NullArguments_AreRefused() {
    Assert.That(() => TraceComparer.Compare(null!, Trace(), _everything), Throws.ArgumentNullException);
    Assert.That(() => TraceComparer.Observed(Trace(), null!), Throws.ArgumentNullException);
    Assert.That(() => TraceComparer.ToDiagnostic("f", "v", null!), Throws.ArgumentNullException);
    Assert.That(() => TraceComparer.CheckReference("f", "v", Trace(), null!), Throws.ArgumentNullException);
  }

  /// <summary>The later of two overlapping blocks wins; an address outside every block has no byte.</summary>
  [Test]
  public void ByteAt_ReadsTheLatestBlock() {
    var state = State(memory: [Bytes(0x10, 1, 2), Bytes(0x11, 9)]);
    Assert.That(new[] { state.ByteAt(0x0F), state.ByteAt(0x10), state.ByteAt(0x11), state.ByteAt(0x12) }, Is.EqualTo(new byte?[] { null, 1, 9, null }));
  }

  /// <summary>The records compare their arrays and dictionaries by content.</summary>
  [Test]
  public void Records_CompareByContent() {
    var full = Full();
    var bytes = Bytes(1, 2);
    var write = Write(1, 2);
    var call = new ExternalCall("f", null, [1], null);
    Assert.That(full, Is.EqualTo(Full()));
    Assert.That(full.GetHashCode(), Is.EqualTo(Full().GetHashCode()));
    Assert.That(Full(), Is.Not.EqualTo(Full(cycles: 1)));
    Assert.That(State([("r0", 1)]), Is.Not.EqualTo(State([("r0", 2)])));
    Assert.That(State([("r0", 1)]), Is.Not.EqualTo(State([("r1", 1)])));
    Assert.That(State([("r0", 1)]), Is.Not.EqualTo(State()));
    Assert.That(State([("r0", 1)]).Equals(null), Is.False);
    Assert.That(bytes, Is.EqualTo(Bytes(1, 2)));
    Assert.That(bytes.GetHashCode(), Is.EqualTo(Bytes(1, 2).GetHashCode()));
    Assert.That(Bytes(1, 2).Equals(null), Is.False);
    Assert.That(_everything, Is.EqualTo(Observe(["r0", "r1"], ["c"], [new(0x1000, 2)], [.. TraceEvent.Kinds], true, TraceMode.Raw)));
    Assert.That(_everything.GetHashCode(), Is.EqualTo(Observe(["r0", "r1"], ["c"], [new(0x1000, 2)], [.. TraceEvent.Kinds], true, TraceMode.Raw).GetHashCode()));
    Assert.That(_everything, Is.Not.EqualTo(TraceProjection.Nothing));
    Assert.That(_everything.Equals(null), Is.False);
    Assert.That(Full().Equals(null), Is.False);
    Assert.That(write.GetHashCode(), Is.EqualTo(Write(1, 2).GetHashCode()));
    Assert.That(Write(1, 2).Equals(null), Is.False);
    Assert.That(call.GetHashCode(), Is.EqualTo(new ExternalCall("f", null, [1], null).GetHashCode()));
    Assert.That(new ExternalCall("f", null, [1], null).Equals(null), Is.False);
  }

  /// <summary>Each event's one-line text.</summary>
  [Test]
  public void Events_HaveText() {
    Assert.That(Write(0x1000, 1, 0xAB).Text, Is.EqualTo("memory-write 0x1000 = 01AB"));
    Assert.That(new DeviceAccess("io", 0x60, 8, false, 0x1).Text, Is.EqualTo("device-read io 0x60 u8 = 0x1"));
    Assert.That(new ExternalCall("f", "cdecl", [1, 0x20], 0).Text, Is.EqualTo("external f cdecl(0x1, 0x20) -> 0x0"));
    Assert.That(new ExternalCall("f", null, [], null).Text, Is.EqualTo("external f() -> none"));
    Assert.That(new Trap("divide-error", 0, 0x1004).Text, Is.EqualTo("trap divide-error vector 0 at 0x1004"));
    Assert.That(new Trap("budget", null, null).Text, Is.EqualTo("trap budget vector none at none"));
    Assert.That(new Return(null).Text, Is.EqualTo("return none"));
    Assert.That(new EmulatorProgram(new byte[] { 0x90 }, 0x400, 0x400, "protected").Mode, Is.EqualTo("protected"));
  }

  [Test]
  public void ProjectionNothing_ComparesNoEvents()
    => Assert.That(TraceProjection.Nothing.Compares(TraceEvent.ReturnKind), Is.False);

  [Test]
  public void ATrapEvent_KeepsItsKind() => Assert.That(new Trap("page-fault", 14, null), Has.Property(nameof(Trap.Kind)).EqualTo("page-fault").And.Property(nameof(TraceEvent.EventKind)).EqualTo("trap"));

  [Test]
  public void NullDiffValues_AreAbsentInTheSame()
    => Assert.That(new object?[] { TraceDiff.Same.What, TraceDiff.Same.Expected, TraceDiff.Same.Actual, TraceDiff.Same.EventIndex }, Is.All.Null);

  [Test]
  public void TheEventKinds_AreTheFive()
    => Assert.That(TraceEvent.Kinds, Is.EqualTo(new[] { "device", "external", "trap", "memory-write", "return" }));

  [Test]
  public void Hex_FormatsWithoutLeadingZeros()
    => Assert.That(new[] { TraceEvent.Hex(0UL), TraceEvent.Hex(0x3C8UL), TraceEvent.Hex((ulong?)null), TraceEvent.Hex(ulong.MaxValue) },
      Is.EqualTo(new[] { "0x0", "0x3C8", "none", "0xFFFFFFFFFFFFFFFF" }));

}
