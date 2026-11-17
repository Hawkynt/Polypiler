using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Regions;
using Polypiler.Tests.MachineSemantics.Support;

namespace Polypiler.Tests.MachineSemantics;

/// <summary>
/// The memory classes of machine accesses (MACH-REGIONS): <see cref="RegionClassifier"/> against the
/// IBM PC map of <c>pc-vga.map.json</c> and provenance facts, and the rules of <see cref="AddressMap"/>.
/// One test per row of the issue's test design and per scenario of <c>mach-regions.feature</c>.
/// </summary>
[TestFixture]
public sealed class RegionClassifierTests {

  private static readonly AddressMap _pcVga = RegionFixtures.Map("pc-vga.map.json");
  private static readonly AddressMap _middle = RegionFixtures.Map("middle.map.json");

  private static MachineFunction Function(string body, string name = "f")
    => RegionFixtures.Parse($"machine test-m32\n\n@{name} {{\nentry:\n{body}\n  Ret\n}}\n").Functions.Single();

  private static (AccessClass Class, DiagnosticBag Diagnostics) First(string body, IAddressMap? map, IProvenanceFacts? facts = null) {
    var function = Function(body);
    var block = function.Blocks[0];
    var access = block.Instructions.First(instruction => instruction.Opcode is MachineOpcode.Load or MachineOpcode.Store);
    var diagnostics = new DiagnosticBag();
    return (RegionClassifier.Classify(access, block, map, facts ?? NoProvenanceFacts.Instance, diagnostics, function.Name), diagnostics);
  }

  private static AccessClass Constant(string access, IAddressMap? map = null) => First("  " + access, map ?? _pcVga).Class;

  private static void IsClass(AccessClass actual, MemoryClass expected, params string[] channels) => Assert.Multiple(() => {
    Assert.That(actual.Class, Is.EqualTo(expected));
    Assert.That(actual.Channels, Is.EqualTo(channels));
  });

  private static IEnumerable<TestCaseData> Fixtures() => RegionFixtures.Files().Select(file => new TestCaseData(file).SetName($"Fixture({file})"));

  /// <summary>Every <c>.mir</c> fixture: the classes written by hand in its comments are what <see cref="RegionClassifier.Annotate"/> gives.</summary>
  [TestCaseSource(nameof(Fixtures))]
  public void Fixture(string file) {
    var fixture = RegionFixtures.Load(file);
    var diagnostics = new DiagnosticBag();
    var classes = RegionClassifier.Annotate(fixture.Module.Functions.Single(), fixture.Map, fixture.Facts, diagnostics);
    Assert.That(classes.Select(access => (access.Class.Class, string.Join(",", access.Class.Channels))),
      Is.EqualTo(fixture.Expected.Select(expected => (expected.Class, string.Join(",", expected.Channels)))));
    Assert.That(diagnostics.Items.Select(diagnostic => diagnostic.Code), Is.EqualTo(fixture.Expected.Where(expected => expected.Warns).Select(_ => "PLY4010")));
  }

  /// <summary>The fixtures together cover all seven classes and all six rules.</summary>
  [Test]
  public void FixturesCoverEveryClass() {
    var seen = RegionFixtures.Files().SelectMany(file => RegionFixtures.Load(file).Expected).Select(expected => expected.Class).ToHashSet();
    Assert.That(seen, Is.EquivalentTo(Enum.GetValues<MemoryClass>()));
  }

  /// <summary>Row 1, happy path: a store through a private allocation of the function is <c>ProvenLocal</c> on <c>Memory:buf</c>.</summary>
  [Test]
  public void Row01_PrivateAllocation() {
    var facts = new ProvenanceFactTable().WithProvenance("f", "buf", new PrivateAllocation("buf"));
    var (actual, _) = First("  %buf = RegRead.u32 @sp\n  %v = Const.u32 7\n  Store.u32 %buf, %v", _pcVga, facts);
    IsClass(actual, MemoryClass.ProvenLocal, "Memory:buf");
    Assert.That(actual.Reason, Is.EqualTo("private allocation 'buf'"));
  }

  /// <summary>Row 2, happy path: a constant address inside the MMIO range is a device access.</summary>
  [Test]
  public void Row02_MmioRange() {
    var actual = Constant("Store.u8 0xA0064, 77");
    IsClass(actual, MemoryClass.MemoryMappedIO, "Device:mmio.vga");
    Assert.That(actual.Reason, Is.EqualTo("map range 'vga'"));
  }

  /// <summary>Row 3, boundary: the last RAM byte is shared memory, the first MMIO byte a device.</summary>
  [Test]
  public void Row03_LastRamFirstMmio() {
    IsClass(Constant("Store.u8 0x9FFFF, 77"), MemoryClass.ProvenShared, "Memory:ram");
    IsClass(Constant("Store.u8 0xA0000, 77"), MemoryClass.MemoryMappedIO, "Device:mmio.vga");
  }

  /// <summary>Row 4, boundary: the last MMIO byte is a device, the first byte after it unknown memory with <c>PLY4010</c>.</summary>
  [Test]
  public void Row04_LastMmioFirstAfter() {
    IsClass(Constant("Store.u8 0xBFFFF, 77"), MemoryClass.MemoryMappedIO, "Device:mmio.vga");
    var (actual, diagnostics) = First("  Store.u8 0xC0000, 77", _pcVga);
    IsClass(actual, MemoryClass.UnknownMemory, MemoryChannels.Unknown);
    Assert.That(actual.Reason, Is.EqualTo("no mapped region at 0xC0000"));
    Assert.That(diagnostics.Items.Single().ToString(),
      Is.EqualTo("warning PLY4010: 'Store.u8 0xC0000, 0x4D' at @f:entry accesses constant address 0xC0000, which no declared region covers; treated as unknown memory"));
  }

  /// <summary>Row 5, boundary: a four-byte store spanning RAM and the video buffer is a device access on both channels.</summary>
  [Test]
  public void Row05_SpanningTwoRanges() {
    var (actual, diagnostics) = First("  %v = Const.u32 0x12345678\n  Store.u32 0x9FFFE, %v", _pcVga);
    IsClass(actual, MemoryClass.MemoryMappedIO, "Memory:ram", "Device:mmio.vga");
    Assert.That(actual.Reason, Is.EqualTo("map ranges 'ram', 'vga'"));
    Assert.That(diagnostics.Items, Is.Empty);
  }

  /// <summary>Row 6, boundary: address 0 and the highest 32-bit address with a map covering neither are unknown memory twice.</summary>
  [Test]
  public void Row06_AddressZeroAndTop() {
    IsClass(Constant("Store.u8 0, 1", _middle), MemoryClass.UnknownMemory, MemoryChannels.Unknown);
    IsClass(Constant("Store.u8 0xFFFFFFFF, 1", _middle), MemoryClass.UnknownMemory, MemoryChannels.Unknown);
  }

  /// <summary>Row 7, equivalence: a write into the executable ROM is <c>ExecutableMemory</c>.</summary>
  [Test]
  public void Row07_WriteIntoExecutable() => IsClass(Constant("Store.u8 0xF0010, 77"), MemoryClass.ExecutableMemory, "Memory.ExecutableWrite:rom");

  /// <summary>Row 8, equivalence: a read from ROM is shared memory.</summary>
  [Test]
  public void Row08_ReadFromRom() => IsClass(Constant("%r = Load.u8 0xF0010"), MemoryClass.ProvenShared, "Memory:rom");

  /// <summary>Row 9, equivalence: a caller-supplied buffer of a declared contract is external memory.</summary>
  [Test]
  public void Row09_CallerBuffer() {
    var facts = new ProvenanceFactTable().WithProvenance("f", "dst", new CallerBuffer("dst", "buffer(len=16)"));
    var (actual, _) = First("  %dst = RegRead.u32 @r0\n  %v = Const.u32 7\n  Store.u32 %dst, %v", _pcVga, facts);
    IsClass(actual, MemoryClass.ExternalMemory, "Memory:ext.dst");
    Assert.That(actual.Reason, Is.EqualTo("caller buffer 'dst' of contract 'buffer(len=16)'"));
  }

  /// <summary>Row 10, equivalence: storage a runtime dependency owns is runtime state.</summary>
  [Test]
  public void Row10_RuntimeStorage() {
    var facts = new ProvenanceFactTable().WithProvenance("f", "errno", new RuntimeStorage("libc"));
    var (actual, _) = First("  %errno = RegRead.u32 @r0\n  %e = Load.u32 %errno", _pcVga, facts);
    IsClass(actual, MemoryClass.RuntimeState, "Runtime.State:libc");
    Assert.That(actual.Reason, Is.EqualTo("storage of runtime 'libc'"));
  }

  /// <summary>Row 11, equivalence: a pointer whose proven range lies inside the video buffer is a device access.</summary>
  [Test]
  public void Row11_RangeInsideOneRegion() {
    var facts = new ProvenanceFactTable().WithRange("f", "p", new(0xA0000, 0xA00FF));
    var (actual, diagnostics) = First("  %p = RegRead.u32 @r0\n  Store.u8 %p, 1", _pcVga, facts);
    IsClass(actual, MemoryClass.MemoryMappedIO, "Device:mmio.vga");
    Assert.That(diagnostics.Items, Is.Empty);
  }

  /// <summary>Row 12, equivalence: a proven range leaving RAM into the video buffer is a device access on both channels.</summary>
  [Test]
  public void Row12_RangeLeavingTheRegion() {
    var facts = new ProvenanceFactTable().WithRange("f", "p", new(0x9FF00, 0xA00FF));
    IsClass(First("  %p = RegRead.u32 @r0\n  Store.u8 %p, 1", _pcVga, facts).Class, MemoryClass.MemoryMappedIO, "Memory:ram", "Device:mmio.vga");
  }

  /// <summary>Row 13, exception: no map and no provenance is unknown memory, reason <c>no provenance</c>.</summary>
  [Test]
  public void Row13_NoMapNoProvenance() {
    var (actual, _) = First("  %p = RegRead.u32 @r0\n  %v = Const.u32 7\n  Store.u32 %p, %v", null);
    IsClass(actual, MemoryClass.UnknownMemory, MemoryChannels.Unknown);
    Assert.That(actual.Reason, Is.EqualTo("no provenance"));
  }

  /// <summary>Row 14, exception: ranges sharing 0x1FFF are refused with <c>PLY4009</c>; ranges that only touch are not.</summary>
  [Test]
  public void Row14_OverlappingRanges() {
    var refused = Assert.Throws<MachineException>(() => _ = new AddressMap([new("a", 0x1000, 0x1FFF, AddressKind.Ram, false), new("b", 0x1FFF, 0x2FFF, AddressKind.Ram, false)]))!;
    Assert.That(refused.Diagnostic.Code, Is.EqualTo("PLY4009"));
    Assert.That(refused.Diagnostic.Message, Is.EqualTo("address ranges 'a' and 'b' overlap at 0x1FFF"));
    Assert.That(new AddressMap([new("b", 0x2000, 0x2FFF, AddressKind.Ram, false), new("a", 0x1000, 0x1FFF, AddressKind.Ram, false)]).Ranges.Select(range => range.Name), Is.EqualTo(new[] { "a", "b" }));
  }

  /// <summary>Row 14, every overlap is reported, also of a range with one that is not its neighbour.</summary>
  [Test]
  public void Row14_EveryOverlapIsValidated() {
    var problems = AddressMap.Validate([new("big", 0, 0xFFFF, AddressKind.Ram, false), new("x", 0x100, 0x1FF, AddressKind.Ram, false), new("y", 0x200, 0x2FF, AddressKind.Ram, false)], 32, "test-m32");
    Assert.That(problems.Select(problem => problem.Message), Is.EqualTo(new[] {
      "address ranges 'big' and 'x' overlap at 0x100", "address ranges 'big' and 'y' overlap at 0x200" }));
  }

  /// <summary>Row 15, exception: an inverted range is refused with <c>PLY4011</c>; a range of one address is not.</summary>
  [Test]
  public void Row15_InvertedRange() {
    var refused = Assert.Throws<MachineException>(() => _ = new AddressMap([new("r", 0x2000, 0x1000, AddressKind.Ram, false)], 32))!;
    Assert.That(refused.Diagnostic.ToString(), Is.EqualTo("error PLY4011: address range 'r' ends before it starts"));
    var one = new AddressMap([new("one", 0x2000, 0x2000, AddressKind.Ram, false)], 32);
    Assert.That(new[] { one.RangeAt(0x1FFF)?.Name, one.RangeAt(0x2000)?.Name, one.RangeAt(0x2001)?.Name }, Is.EqualTo(new[] { null, "one", null }));
  }

  /// <summary>Row 15, boundary: a range ending at the top of the 32-bit space is accepted, one address beyond it refused.</summary>
  [Test]
  public void Row15_OversizedRange() {
    Assert.That(new AddressMap([new("top", 0xFFFF0000, 0xFFFFFFFF, AddressKind.Ram, false)], 32, "test-m32").RangeAt(0xFFFFFFFF)?.Name, Is.EqualTo("top"));
    var refused = Assert.Throws<MachineException>(() => _ = new AddressMap([new("r", 0, 0x1_0000_0000, AddressKind.Ram, false)], 32, "test-m32"))!;
    Assert.That(refused.Diagnostic.ToString(), Is.EqualTo("error PLY4011: address range 'r' exceeds the 32-bit address space of 'test-m32'"));
    Assert.That(new AddressMap([new("all", 0, ulong.MaxValue, AddressKind.Ram, false)]).RangeAt(ulong.MaxValue)?.Name, Is.EqualTo("all"));
  }

  /// <summary>Scenario "A constant address is classified by the memory map", its four examples.</summary>
  [TestCase("0x9FFFF", MemoryClass.ProvenShared, "Memory:ram")]
  [TestCase("0xA0000", MemoryClass.MemoryMappedIO, "Device:mmio.vga")]
  [TestCase("0xBFFFF", MemoryClass.MemoryMappedIO, "Device:mmio.vga")]
  [TestCase("0xC0000", MemoryClass.UnknownMemory, "Memory.Unknown")]
  public void Scenario_ConstantAddress(string address, MemoryClass expected, string channel)
    => IsClass(Constant($"Store.u8 {address}, 77"), expected, channel);

  /// <summary>Scenario "A store through a pointer without provenance is unknown memory".</summary>
  [Test]
  public void Scenario_PointerWithoutProvenance() => Assert.That(First("  %p = RegRead.u32 @r0\n  %v = RegRead.u32 @r1\n  Store.u32 %p, %v", null).Class.Reason, Is.EqualTo("no provenance"));

  /// <summary>Scenario "A four-byte store crossing into the video buffer is a device access".</summary>
  [Test]
  public void Scenario_CrossingIntoVideo() => IsClass(First("  %v = RegRead.u32 @r1\n  Store.u32 0x9FFFE, %v", _pcVga).Class, MemoryClass.MemoryMappedIO, "Memory:ram", "Device:mmio.vga");

  /// <summary>Scenario "Overlapping ranges are refused".</summary>
  [Test]
  public void Scenario_OverlapRefused()
    => Assert.That(AddressMap.Validate([new("a", 0x1000, 0x1FFF, AddressKind.Ram, false), new("b", 0x1FFF, 0x2FFF, AddressKind.Ram, false)], 32, "test-m32").Single().Message,
      Is.EqualTo("address ranges 'a' and 'b' overlap at 0x1FFF"));

  /// <summary>Equivalence: an address a <c>Const</c> of the same block defines is a constant; one defined by another opcode is not.</summary>
  [Test]
  public void ConstValueIsAConstantAddress() {
    IsClass(First("  %a = Const.u32 0xA0002\n  %w = Load.u16 %a", _pcVga).Class, MemoryClass.MemoryMappedIO, "Device:mmio.vga");
    IsClass(First("  %a = RegRead.u32 @r0\n  %w = Load.u16 %a", _pcVga).Class, MemoryClass.UnknownMemory, MemoryChannels.Unknown);
  }

  /// <summary>Equivalence: a constant address without a map is unknown memory, and draws no <c>PLY4010</c>: nothing was declared.</summary>
  [Test]
  public void ConstantWithoutMap() {
    var (actual, diagnostics) = First("  Store.u8 0xA0000, 77", null);
    IsClass(actual, MemoryClass.UnknownMemory, MemoryChannels.Unknown);
    Assert.That(actual.Reason, Is.EqualTo("constant address 0xA0000 and no address map"));
    Assert.That(diagnostics.Items, Is.Empty);
  }

  /// <summary>Equivalence: a map range wins over provenance, as rule 1 comes first; without a range fact the provenance decides.</summary>
  [Test]
  public void MapRangeComesBeforeProvenance() {
    var facts = new ProvenanceFactTable().WithRange("f", "p", new(0xA0000, 0xA0000)).WithProvenance("f", "p", new GlobalAllocation("table"));
    IsClass(First("  %p = RegRead.u32 @r0\n  Store.u8 %p, 1", _pcVga, facts).Class, MemoryClass.MemoryMappedIO, "Device:mmio.vga");
    IsClass(First("  %p = RegRead.u32 @r0\n  Store.u8 %p, 1", null, facts).Class, MemoryClass.ProvenShared, "Memory:table");
  }

  /// <summary>Boundary: a range fact running out of every region is unknown memory on its channels, without <c>PLY4010</c> (it is no constant).</summary>
  [Test]
  public void RangeFactLeavingTheMap() {
    var facts = new ProvenanceFactTable().WithRange("f", "p", new(0xBFF00, 0xF0000));
    var (actual, diagnostics) = First("  %p = RegRead.u32 @r0\n  Store.u8 %p, 1", _pcVga, facts);
    IsClass(actual, MemoryClass.MemoryMappedIO, "Device:mmio.vga", MemoryChannels.Unknown, "Memory.ExecutableWrite:rom");
    Assert.That(actual.Reason, Is.EqualTo("map ranges 'vga', 'rom'; no mapped region at 0xC0000"));
    Assert.That(diagnostics.Items, Is.Empty);
  }

  /// <summary>Equivalence: an access inside an <c>Unmapped</c> range is unknown memory with <c>PLY4010</c> at the accessed address; one running into it, at the range's start.</summary>
  [Test]
  public void UnmappedRange() {
    var (inside, diagnostics) = First("  Store.u8 0x2004, 1", _middle);
    IsClass(inside, MemoryClass.UnknownMemory, MemoryChannels.Unknown);
    Assert.That(inside.Reason, Is.EqualTo("map range 'hole'; no mapped region at 0x2004"));
    Assert.That(diagnostics.Items.Single().Message, Does.Contain("constant address 0x2004"));
    var (into, warnings) = First("  %x = Load.u32 0x1FFE", _middle);
    IsClass(into, MemoryClass.UnknownMemory, "Memory:low", MemoryChannels.Unknown);
    Assert.That(warnings.Items.Single().Message, Does.Contain("constant address 0x2000"));
  }

  /// <summary>Equivalence: ordinary ranges that only executable code may run from are shared memory when read, and the stack range is shared memory.</summary>
  [Test]
  public void StackAndExecutableRam() {
    var map = new AddressMap([new("code", 0, 0xFF, AddressKind.Ram, true), new("stack", 0x100, 0x1FF, AddressKind.Stack, false)], 32);
    IsClass(Constant("%x = Load.u8 0x10", map), MemoryClass.ProvenShared, "Memory:code");
    IsClass(Constant("Store.u8 0x10, 1", map), MemoryClass.ExecutableMemory, "Memory.ExecutableWrite:code");
    IsClass(Constant("Store.u8 0x100, 1", map), MemoryClass.ProvenShared, "Memory:stack");
  }

  /// <summary>Boundary: an access at the top of a 64-bit space that wraps around is unknown in its wrapped bytes; one that ends exactly at the top is not.</summary>
  [Test]
  public void WrapAtTheTopOfTheAddressSpace() {
    var map = new AddressMap([new("top", 0xFFFF_FFFF_FFFF_FF00, ulong.MaxValue, AddressKind.Ram, false)]);
    var near = new ProvenanceFactTable().WithRange("f", "p", new(ulong.MaxValue - 3, ulong.MaxValue - 3));
    IsClass(First("  %p = RegRead.u32 @r0\n  %x = Load.u32 %p", map, near).Class, MemoryClass.ProvenShared, "Memory:top");
    var wrapping = new ProvenanceFactTable().WithRange("f", "p", new(ulong.MaxValue - 1, ulong.MaxValue));
    var (actual, _) = First("  %p = RegRead.u32 @r0\n  %x = Load.u32 %p", map, wrapping);
    IsClass(actual, MemoryClass.UnknownMemory, "Memory:top", MemoryChannels.Unknown);
    Assert.That(actual.Reason, Is.EqualTo("map range 'top'; no mapped region at 0"));
  }

  /// <summary><c>PLY4010</c> names the machine-code address of an access with an origin, the block when the function has no name, and is dropped without a sink.</summary>
  [Test]
  public void WhereTheWarningPoints() {
    var function = Function("  Store.u8 0xC0000, 77 !at(0x00401000, \"C6 05\")");
    var block = function.Blocks[0];
    var bag = new DiagnosticBag();
    RegionClassifier.Classify(block.Instructions[0], block, _pcVga, NoProvenanceFacts.Instance, bag, "f");
    RegionClassifier.Classify(block.Instructions[0] with { Origin = null }, block with { Instructions = [block.Instructions[0] with { Origin = null }] }, _pcVga, NoProvenanceFacts.Instance, bag);
    Assert.That(bag.Items.Select(item => item.Message.Split(" accesses")[0]), Is.EqualTo(new[] { "'Store.u8 0xC0000, 0x4D' at 0x401000", "'Store.u8 0xC0000, 0x4D' at entry" }));
    Assert.That(RegionClassifier.Classify(block.Instructions[0], block, _pcVga, NoProvenanceFacts.Instance).Class, Is.EqualTo(MemoryClass.UnknownMemory));
  }

  /// <summary><see cref="RegionClassifier.Annotate"/> visits every block and keys each access by block and index.</summary>
  [Test]
  public void AnnotateKeysByBlockAndIndex() {
    var function = RegionFixtures.Parse("machine test-m32\n\n@g {\nentry:\n  %x = Load.u8 0xA0000\n  Jump next\nnext:\n  %c = Const.u32 1\n  Store.u8 0x10, 1\n  Ret\n}\n").Functions.Single();
    var classes = RegionClassifier.Annotate(function, _pcVga, NoProvenanceFacts.Instance);
    Assert.That(classes.Select(access => (access.Block, access.Index, access.Class.Class)), Is.EqualTo(new[] {
      ("entry", 0, MemoryClass.MemoryMappedIO), ("next", 1, MemoryClass.ProvenShared) }));
  }

  /// <summary>Exception: what is no access, accesses no whole bytes, has no address, or is not in the block is refused.</summary>
  [Test]
  public void InvalidAccessesAreRefused() {
    var block = Function("  %x = Load.u8 0x10").Blocks[0];
    MachineBlock With(MachineInstruction instruction) => new("entry", [instruction]);
    var notAccess = MachineInstruction.Create(MachineOpcode.Const, MachineType.U8, "x", new MachineImmediate(1));
    var nibble = MachineInstruction.Create(MachineOpcode.Load, MachineType.Of(4), "x", new MachineImmediate(1));
    var none = MachineInstruction.Create(MachineOpcode.Store, MachineType.Void, null);
    var empty = MachineInstruction.Create(MachineOpcode.Load, MachineType.U8, "x");
    var storage = MachineInstruction.Create(MachineOpcode.Load, MachineType.U8, "x", new MachineStorage("r0"));
    var facts = NoProvenanceFacts.Instance;
    Assert.Multiple(() => {
      Assert.That(() => RegionClassifier.Classify(notAccess, With(notAccess), null, facts), Throws.ArgumentException.With.Message.Contains("no memory access"));
      Assert.That(() => RegionClassifier.Classify(nibble, With(nibble), null, facts), Throws.ArgumentException.With.Message.Contains("no whole number of bytes"));
      Assert.That(() => RegionClassifier.Classify(none, With(none), null, facts), Throws.ArgumentException.With.Message.Contains("no whole number of bytes"));
      Assert.That(() => RegionClassifier.Classify(empty, With(empty), null, facts), Throws.ArgumentException.With.Message.Contains("has no address"));
      Assert.That(() => RegionClassifier.Classify(storage, With(storage), null, facts), Throws.ArgumentException.With.Message.Contains("has no address"));
      Assert.That(() => RegionClassifier.Classify(nibble, block, null, facts), Throws.ArgumentException.With.Message.Contains("is not in block 'entry'"));
      Assert.That(() => RegionClassifier.Classify(null!, block, null, facts), Throws.ArgumentNullException);
      Assert.That(() => RegionClassifier.Classify(block.Instructions[0], null!, null, facts), Throws.ArgumentNullException);
      Assert.That(() => RegionClassifier.Classify(block.Instructions[0], block, null, null!), Throws.ArgumentNullException);
      Assert.That(() => RegionClassifier.Annotate(null!, null, facts), Throws.ArgumentNullException);
    });
  }

  /// <summary>The fact records: a proven range cannot be inverted; the tables answer what they were given and nothing else.</summary>
  [Test]
  public void FactTables() {
    Assert.That(() => new ProvenRange(2, 1), Throws.ArgumentException.With.Message.Contains("before its start"));
    var range = new ProvenRange(5, 5);
    var table = new ProvenanceFactTable().WithRange("f", "p", range).WithProvenance("f", "q", new GlobalAllocation("g"));
    Assert.Multiple(() => {
      Assert.That(table.RangeOf("f", "p"), Is.SameAs(range));
      Assert.That(table.RangeOf("g", "p"), Is.Null);
      Assert.That(table.ProvenanceOf("f", "q"), Is.EqualTo(new GlobalAllocation("g")));
      Assert.That(table.ProvenanceOf("f", "p"), Is.Null);
      Assert.That(NoProvenanceFacts.Instance.RangeOf("f", "p"), Is.Null);
      Assert.That(NoProvenanceFacts.Instance.ProvenanceOf("f", "p"), Is.Null);
      Assert.That(() => table.WithRange("f", "p", null!), Throws.ArgumentNullException);
      Assert.That(() => table.WithProvenance("f", "p", null!), Throws.ArgumentNullException);
    });
  }

  /// <summary>An access class compares its channels by value and prints class, channels and reason.</summary>
  [Test]
  public void AccessClassEquality() {
    var a = new AccessClass(MemoryClass.ProvenShared, ["Memory:ram"], "map range 'ram'");
    var b = new AccessClass(MemoryClass.ProvenShared, ["Memory:ram"], "map range 'ram'");
    Assert.That(a.ToString(), Is.EqualTo("ProvenShared [Memory:ram] (map range 'ram')"));
    Assert.Multiple(() => {
      Assert.That(a, Is.EqualTo(b));
      Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
      Assert.That(a.Equals(null), Is.False);
      Assert.That(a, Is.Not.EqualTo(b with { Channels = ["Memory:rom"] }));
      Assert.That(a, Is.Not.EqualTo(b with { Class = MemoryClass.ProvenLocal }));
      Assert.That(a, Is.Not.EqualTo(b with { Reason = "other" }));
    });
  }

}
