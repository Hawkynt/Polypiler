using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.BinaryFormats;
using Polypiler.Core.BinaryFormats.Discovery;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Ports;
using Polypiler.Core.MachineSemantics.Regions;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Fuzz;

namespace Polypiler.Tests.BinaryFormats.Discovery;

/// <summary>
/// Recursive-traversal discovery (#942) on the toy ISA: the hand-derived fixtures of
/// <c>tests/fixtures/native/mach-bin-discovery</c>, and every row of the test design they do not show.
/// </summary>
[TestFixture]
public sealed class DiscoveryTests {

  private static readonly string _fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "native", "mach-bin-discovery");

  private static readonly ToyIsa _toy = new();

  private static DiscoveryResult Run(string hex, ulong[] entries, DiscoveryOptions? options = null, bool resolve = false, params ulong[] symbols)
    => Core.BinaryFormats.Discovery.Discovery.Run(ToyIsa.Load(ToyIsa.Bytes(hex), 0x1000, entries, symbols), _toy, resolve ? _toy : null, options);

  private static IEnumerable<TestCaseData> Fixtures()
    => Directory.EnumerateFiles(_fixtures, "*.hex.txt").Order(StringComparer.Ordinal).Select(path => new TestCaseData(Path.GetFileName(path)[..^".hex.txt".Length]));

  /// <summary>A fixture: the listing's bytes at 0x1000, its <c>@entries</c>, <c>@symbols</c> and <c>@resolver</c>, against the expected file written by hand.</summary>
  [TestCaseSource(nameof(Fixtures))]
  public void EachFixture_GivesItsHandDerivedResult(string name) {
    var lines = File.ReadAllLines(Path.Combine(_fixtures, name + ".hex.txt"));
    ulong[] Directive(string key) => [.. lines.Where(line => line.StartsWith(key + " ", StringComparison.Ordinal))
      .SelectMany(line => line[(key.Length + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)).Select(text => Convert.ToUInt64(text, 16))];
    var hex = string.Join(' ', lines.Where(line => line.Length > 0 && line[0] is not ('#' or '@')));
    var result = Run(hex, Directive("@entries"), null, lines.Contains("@resolver toy"), Directive("@symbols"));
    var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(_fixtures, name + ".expected.json")))!;

    Assert.That(JsonNode.DeepEquals(DiscoveryReport.Write(result), expected), Is.True, DiscoveryReport.Text(result));
  }

  [Test]
  public void TheScenarioDiagnostics_NameTheirAddresses() {
    Assert.That(Run("05 00 00 01", [0x1000]).Diagnostics.Single().Message, Is.EqualTo("indirect jump at 0x1000 could not be resolved; code reachable only through it stays undecoded"));
    Assert.That(Run("02 00 02 00 01", [0x1000, 0x1003]).Diagnostics.Single().Message, Is.EqualTo("byte 0x1003 belongs to instructions starting at 0x1002 and at 0x1003"));
    Assert.That(Run("00 FF 01", [0x1000]).Diagnostics.Single().Message, Is.EqualTo("bytes at 0x1001 reached from 0x1000 do not decode as toy toy; '0x1000' is incomplete"));
    var unresolved = Run("05 00 00 01", [0x1000]);
    Assert.That(Enumerable.Range(0x1001, 3).Select(address => unresolved.Bytes[(ulong)address]), Is.All.EqualTo(ByteClass.Undecoded));
  }

  [Test]
  public void AHeuristicCandidate_IsTraversedOnlyWhenAccepted() {
    var binary = ToyIsa.Load(ToyIsa.Bytes("01 FF FF FF 00 01"), 0x1000, [0x1000]) with { Hints = [0x1004] };
    var refused = Core.BinaryFormats.Discovery.Discovery.Run(binary, _toy, null);
    var accepted = Core.BinaryFormats.Discovery.Discovery.Run(binary, _toy, null, new DiscoveryOptions(AcceptHeuristic: true));

    Assert.That(refused.Functions.Select(function => function.Entry), Is.EqualTo(new ulong[] { 0x1000 }));
    Assert.That(refused.Bytes[0x1004], Is.EqualTo(ByteClass.Undecoded));
    var hinted = accepted.Functions.Single(function => function.Entry == 0x1004);
    Assert.That((hinted.Confidence, hinted.Evidence, hinted.Blocks.Single().End), Is.EqualTo((Confidence.Heuristic, "hint", 0x1005UL)));
  }

  [Test]
  public void AnEntryAtTheLastByte_IsOneBlockOfOneByte() {
    var block = Run("FF FF 01", [0x1002]).Functions.Single().Blocks.Single();

    Assert.That((block.Start, block.End), Is.EqualTo((0x1002UL, 0x1002UL)));
  }

  [Test]
  public void AnInstructionRunningPastTheSection_DoesNotDecode() {
    var result = Run("00 02", [0x1000]);

    Assert.That(result.Diagnostics.Single().Code, Is.EqualTo("PLY4071"));
    Assert.That(result.Functions.Single().Complete, Is.False);
  }

  [Test]
  public void AJumpToTheFirstByteAfterTheSection_LeavesTheCode() {
    var result = Run("00 02 00", [0x1000]);

    Assert.That(result.Diagnostics.Single().Message, Is.EqualTo("control flow from 0x1001 leaves the code at 0x1003"));
    Assert.That(result.Functions.Single().Blocks.Single().ExternalTargets, Is.EqualTo(new ulong[] { 0x1003 }));
    Assert.That(DiscoveryReport.Write(result)["functions"]![0]!["blocks"]![0]!["external"]!.ToJsonString(), Is.EqualTo("[\"0x1003\"]"));
  }

  [Test]
  public void FlowRunningOffTheSection_LeavesTheCode()
    => Assert.That(Run("00 00", [0x1000]).Diagnostics.Single().Message, Is.EqualTo("control flow from 0x1001 leaves the code at 0x1002"));

  [TestCase(3, true)]
  [TestCase(4, false)]
  public void TheInstructionBudget_IsExactlyReachedOrExceededByOne(int instructions, bool completes) {
    var hex = string.Join(' ', Enumerable.Repeat("00", instructions - 1).Append("01"));
    var result = Run(hex, [0x1000], new DiscoveryOptions(MaxInstructions: 3));

    Assert.That(result.Functions.Single().Complete, Is.EqualTo(completes));
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), completes ? Is.Empty : Is.EqualTo(new[] { "PLY4074" }));
    if (!completes)
      Assert.That((result.Diagnostics[0].Severity, result.Diagnostics[0].Message), Is.EqualTo((DiagnosticSeverity.Error, "discovery stopped after 3 instructions")));
  }

  [Test]
  public void ARecursiveCall_GoesOnOnceTheFunctionReturnsOnAnotherPath() {
    // 0x1000 jcc 0x1005; 0x1002 call 0x1000 (itself: it waits); 0x1004 nop, reached only after the wait; 0x1005 ret
    var result = Run("03 03 04 FC 00 01", [0x1000]);

    Assert.That(result.Bytes[0x1004], Is.EqualTo(ByteClass.Code));
    Assert.That(result.Functions.Single().Blocks.Select(block => (block.Start, block.End)), Is.EqualTo(new[] { (0x1000UL, 0x1001UL), (0x1002UL, 0x1004UL), (0x1005UL, 0x1005UL) }));
  }

  [Test]
  public void MutuallyRecursiveCallsWithoutAReturnBeforeThem_NeverGoOn() {
    // 0x1000 call 0x1004; 0x1002 ret; 0x1004 call 0x1000; 0x1006 ret: each returns only if the other does
    var result = Run("04 02 01 FF 04 FA 01", [0x1000]);

    Assert.That((result.Bytes[0x1002], result.Bytes[0x1006]), Is.EqualTo((ByteClass.Undecoded, ByteClass.Undecoded)));
  }

  [Test]
  public void ATailCallToAFunctionThatReturns_MakesTheCallerReturn() {
    // 0x1000 call 0x1004; 0x1002 ret; 0x1004 jmp 0x1007 (the symbol f_1007, a tail call); 0x1007 ret
    var result = Run("04 02 01 FF 02 01 FF 01", [0x1000], null, false, 0x1007);

    Assert.That(result.Functions.First().Blocks.Single().End, Is.EqualTo(0x1002UL));
    Assert.That(result.Functions.Single(function => function.Entry == 0x1004).Blocks.Single().TailCall, Is.EqualTo(0x1007UL));
  }

  [Test]
  public void ATailCallToAFunctionThatNeverReturns_KeepsTheFallThroughUndecoded() {
    // 0x1000 call 0x1004; 0x1004 jmp 0x1007 (tail call to f_1007); f_1007 calls itself then traps
    var result = Run("04 02 00 FF 02 01 FF 04 FE 06", [0x1000], null, false, 0x1007);

    Assert.That(result.Bytes[0x1002], Is.EqualTo(ByteClass.Undecoded));
  }

  [Test]
  public void TailCallsInACycleWithoutAReturn_NeverReturn() {
    // f_1000 jmp f_1004; f_1004 jmp f_1000; the entry 0x1006 calls f_1000, so 0x1008 is never reached
    var result = Run("02 02 FF FF 02 FA 04 F8 01", [0x1006], null, false, 0x1000, 0x1004);

    Assert.That(result.Bytes[0x1008], Is.EqualTo(ByteClass.Undecoded));
    Assert.That(result.Functions.Select(function => function.Blocks.Single().TailCall), Is.EqualTo(new ulong?[] { 0x1004, 0x1000, null }));
  }

  [Test]
  public void TheFuzzTarget_FailsWhenTwoRunsDiffer() {
    var target = new DiscoveryToyFuzzTarget();

    Assert.That(target.Id, Is.EqualTo("discovery-toy"));
    Assert.DoesNotThrow(() => target.Execute([]));
    Assert.Throws<InvalidOperationException>(() => DiscoveryToyFuzzTarget.Same(Run("01", [0x1000]), Run("00 01", [0x1000])));
    Assert.Throws<ArgumentNullException>(() => DiscoveryToyFuzzTarget.Same(null!, Run("01", [0x1000])));
  }

  private static LoadedBinary WithImport(string hex, ulong slot) {
    var binary = ToyIsa.Load(ToyIsa.Bytes(hex), 0x1000, [0x1000]);
    return binary with { Image = binary.Image with { Imports = [new ImageImport(slot, "puts", "libc", null)] } };
  }

  [Test]
  public void ACallOrTailJumpToAnImportSlot_IsExternalWithoutAFinding() {
    var call = Core.BinaryFormats.Discovery.Discovery.Run(WithImport("04 10 01", 0x1012), _toy, null);
    var tail = Core.BinaryFormats.Discovery.Discovery.Run(WithImport("04 02 01 FF 02 10", 0x1016), _toy, null);

    Assert.That(call.Diagnostics, Is.Empty);
    Assert.That(call.Functions.Single().Blocks.Single(), Has.Property(nameof(DiscoveredBlock.ExternalTargets)).EqualTo(new ulong[] { 0x1012 }).And.Property(nameof(DiscoveredBlock.End)).EqualTo(0x1002UL));
    Assert.That(tail.Diagnostics, Is.Empty);
    Assert.That(tail.Functions[0].Blocks.Single().End, Is.EqualTo(0x1002UL), "the callee jumps to an import, which returns");
  }

  [Test]
  public void ACallOutsideTheCode_EndsThePath() {
    var result = Run("04 10 01", [0x1000]);

    Assert.That(result.Diagnostics.Single().Code, Is.EqualTo("PLY4073"));
    Assert.That(result.Bytes[0x1002], Is.EqualTo(ByteClass.Undecoded));
  }

  [Test]
  public void AnEntryOutsideTheCode_IsReported()
    => Assert.That(Run("01", [0x2000]).Diagnostics.Single().Message, Is.EqualTo("control flow from 0x2000 leaves the code at 0x2000"));

  /// <summary>An ISA of one indirect call, <c>08</c>, and the toy's instructions otherwise; and one whose table targets leave the code.</summary>
  private sealed class Indirect(bool callWithoutTarget) : IDisassembler, IJumpTableResolver {

    public string Isa => "toy";

    public DisassembledInstruction? Disassemble(IMachineCodeSource source, ulong address, string mode, IDiagnosticSink sink)
      => source.ByteAt(address) == 0x08 ? new(address, 1, callWithoutTarget ? FlowKind.Call : FlowKind.IndirectCall, [], true, "call *", (byte)0x08)
        : _toy.Disassemble(source, address, mode, sink);

    public JumpTable? Resolve(BinaryImage image, IReadOnlyList<DisassembledInstruction> block, DisassembledInstruction jump)
      => new([0x9000], jump.Address + 1, 0, "out of the code");

  }

  [TestCase(false)]
  [TestCase(true)]
  public void AnIndirectCall_GoesOnAndKeepsItsCalleeUnresolved(bool callWithoutTarget) {
    var result = Core.BinaryFormats.Discovery.Discovery.Run(ToyIsa.Load(ToyIsa.Bytes("08 01"), 0x1000, [0x1000]), new Indirect(callWithoutTarget), null);
    var block = result.Functions.Single().Blocks.Single();

    Assert.That((block.UnresolvedCall, block.End), Is.EqualTo((true, 0x1001UL)));
  }

  [Test]
  public void ATableWithTargetsOutsideTheCode_IsNotValidated() {
    var indirect = new Indirect(false);
    var result = Core.BinaryFormats.Discovery.Discovery.Run(ToyIsa.Load(ToyIsa.Bytes("05"), 0x1000, [0x1000]), indirect, indirect);

    Assert.That(result.Functions.Single().Blocks.Single().Unresolved, Is.True);
    Assert.That(result.Diagnostics.Single().Code, Is.EqualTo("PLY4070"));
  }

  [Test]
  public void AManifestEntryAndASymbolAtOneAddress_IsOneFunctionNamedByTheManifest() {
    var binary = ToyIsa.Load(ToyIsa.Bytes("01"), 0x1000, [0x1000], 0x1000) with {
      Entries = [new EntryContract("main", 0x1000, "toy", [], null, [])]
    };
    var function = Core.BinaryFormats.Discovery.Discovery.Run(binary, _toy, null).Functions.Single();

    Assert.That((function.Name, function.Evidence, function.Confidence), Is.EqualTo(("main", "manifest", Confidence.Proven)));
  }

  [Test]
  public void AFunctionThatReusesAConflictingInstruction_IsIncomplete() {
    // entries 0x1000 and 0x1003 overlap at 0x1003; a third entry 0x1005 jumps back into 0x1003's instruction
    var result = Run("02 00 02 00 01 02 FB", [0x1000, 0x1003, 0x1005]);

    Assert.That(result.Functions.Single(function => function.Entry == 0x1005).Complete, Is.False);
  }

  [Test]
  public void ACallToAnAlreadyKnownFunction_ReusesIt() {
    var result = Run("04 02 04 00 01", [0x1000], null, false, 0x1004);

    Assert.That(result.Functions.Select(function => (function.Entry, function.Confidence)), Is.EqualTo(new[] { (0x1000UL, Confidence.Proven), (0x1004UL, Confidence.Proven) }));
  }

  [Test]
  public void TheResult_IsDeterministic_WhateverTheOrderOfTheSeeds() {
    var binary = ToyIsa.Load(ToyIsa.Bytes("03 01 01 04 02 00 01 01"), 0x1000, [0x1006, 0x1000], 0x1003);
    var reversed = binary with { Image = binary.Image with { Entries = [0x1000, 0x1006] } };
    var first = Core.BinaryFormats.Discovery.Discovery.Run(binary, _toy, _toy);

    Assert.That(Core.BinaryFormats.Discovery.Discovery.Run(binary, _toy, _toy), Is.EqualTo(first));
    Assert.That(DiscoveryReport.Text(Core.BinaryFormats.Discovery.Discovery.Run(reversed, _toy, _toy)), Is.EqualTo(DiscoveryReport.Text(first)));
  }

  /// <summary>Test design 16: 10 000 seeded toy images end within the budget, never crash, and give the same result twice.</summary>
  [Test]
  public void TenThousandSeededToyImages_TerminateWithoutCrashingAndDeterministically() {
    var random = new Random(1);
    var target = new DiscoveryToyFuzzTarget();
    for (var iteration = 0; iteration < 10_000; ++iteration) {
      var bytes = new byte[random.Next(0, 24)];
      random.NextBytes(bytes);
      for (var index = 0; index < bytes.Length; ++index)
        if (random.Next(3) > 0)
          bytes[index] %= 8;
      Assert.DoesNotThrow(() => target.Execute(bytes), Convert.ToHexString(bytes));
    }
  }

  [Test]
  public void TheToyIsa_DecodesEveryOpcode() {
    var sink = new DiagnosticBag();
    var image = ToyIsa.Load(ToyIsa.Bytes("00 01 02 FE 03 00 04 7F 05 06 07 01 80 FF"), 0x1000, []).Image;
    var decoded = new ulong[] { 0x1000, 0x1001, 0x1002, 0x1004, 0x1006, 0x1008, 0x1009, 0x100A }.Select(address => _toy.Disassemble(image, address, "toy", sink)!).ToList();

    Assert.That(decoded.Select(instruction => instruction.Text), Is.EqualTo(new[] { "nop", "ret", "jmp 0x1002", "jcc 0x1006", "call 0x1087", "jmp *", "trap", "jmp table[1]" }));
    Assert.That(decoded[7].Length, Is.EqualTo(3));
    Assert.That(_toy.Disassemble(image, 0x100D, "toy", sink), Is.Null);
    Assert.That(sink.Items.Single().Message, Is.EqualTo("byte FF at 0x100D is no toy instruction"));
    Assert.That(_toy.Disassemble(image, 0x2000, "toy", sink), Is.Null);
    var truncated = ToyIsa.Load(ToyIsa.Bytes("07 02 00"), 0x1000, []).Image;
    Assert.That(_toy.Disassemble(truncated, 0x1000, "toy", sink), Is.Null);
    Assert.That(_toy.Resolve(image, [], decoded[5]), Is.Null);
    Assert.That(_toy.Resolve(image, [], decoded[7]), Is.EqualTo(new JumpTable([0x100D - 128], 0x100C, 1, "inline table of 1 entries bounded by its count")).Using<JumpTable>((a, b) => a.Targets.SequenceEqual(b.Targets) && a.TableAddress == b.TableAddress && a.TableBytes == b.TableBytes && a.Evidence == b.Evidence));
  }

  [Test]
  public void TheByteMapAndTheResults_CompareByContent() {
    var first = Run("04 02 01 FF 00 01", [0x1000]);
    var second = Run("04 02 01 FF 00 01", [0x1000]);

    Assert.That(first.Bytes, Is.EqualTo(second.Bytes));
    Assert.That(first.Bytes.Equals((object)second.Bytes), Is.True);
    Assert.That(first.Bytes.GetHashCode(), Is.EqualTo(second.Bytes.GetHashCode()));
    Assert.That(first.Bytes.Count(ByteClass.Code), Is.EqualTo(5));
    Assert.That(first.GetHashCode(), Is.EqualTo(second.GetHashCode()));
    Assert.That(first.Functions[0].GetHashCode(), Is.EqualTo(second.Functions[0].GetHashCode()));
    Assert.That(first.Functions[0].Blocks[0].GetHashCode(), Is.EqualTo(second.Functions[0].Blocks[0].GetHashCode()));
    Assert.That(first.Equals(null), Is.False);
    Assert.That(first.Functions[0].Equals(null), Is.False);
    Assert.That(first.Functions[0].Blocks[0].Equals(null), Is.False);
    Assert.That(first.Bytes.Equals(null), Is.False);
    Assert.That(first, Is.Not.EqualTo(Run("01", [0x1000])));
  }

  [Test]
  public void TheHintsOfABinary_TakePartInItsEquality() {
    var binary = ToyIsa.Load(ToyIsa.Bytes("01"), 0x1000, [0x1000]);

    Assert.That(binary with { Hints = [0x1000] }, Is.Not.EqualTo(binary));
    Assert.That(binary with { }, Is.EqualTo(binary));
  }

  [Test]
  public void NullArguments_AreRefused() {
    var binary = ToyIsa.Load(ToyIsa.Bytes("01"), 0x1000, [0x1000]);

    Assert.Throws<ArgumentNullException>(() => Core.BinaryFormats.Discovery.Discovery.Run(null!, _toy, null));
    Assert.Throws<ArgumentNullException>(() => Core.BinaryFormats.Discovery.Discovery.Run(binary, null!, null));
    Assert.Throws<ArgumentNullException>(() => DiscoveryReport.Write(null!));
    Assert.Throws<ArgumentNullException>(() => ToyIsa.Load(null!, 0, []));
    Assert.Throws<ArgumentNullException>(() => ToyIsa.Load([], 0, null!));
    Assert.Throws<ArgumentNullException>(() => ToyIsa.Load([], 0, [], null!));
    Assert.Throws<ArgumentNullException>(() => ToyIsa.Bytes(null!));
    Assert.Throws<ArgumentNullException>(() => _toy.Disassemble(null!, 0, "toy", new DiagnosticBag()));
    Assert.Throws<ArgumentNullException>(() => _toy.Disassemble(binary.Image, 0, "toy", null!));
    Assert.Throws<ArgumentNullException>(() => _toy.Resolve(null!, [], null!));
    Assert.Throws<ArgumentNullException>(() => _toy.Resolve(binary.Image, [], null!));
  }

}
