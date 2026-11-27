using System;
using System.Buffers.Binary;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.BinaryFormats;
using Polypiler.Core.MachineSemantics;

namespace Polypiler.Tests.BinaryFormats;

/// <summary>Boundary and structural validation through the registered ELF codec.</summary>
[TestFixture]
public sealed class ElfCodecTests {

  [TestCase(false, false, "x86", "protected32")]
  [TestCase(true, false, "x64", "long64")]
  [TestCase(false, true, "arm32", "arm")]
  [TestCase(true, true, "aarch64", "a64")]
  public void ReadsCompleteHeadersWithoutTables(bool wide, bool big, string isa, string mode) {
    var data = Header(wide, big);
    var result = Read(data);
    Assert.That(result.Problems, Is.Empty);
    var image = result.Value!;
    Assert.That((image.Container, image.Isa, image.Mode, image.ByteOrder), Is.EqualTo(("elf", isa, mode, big ? ByteOrder.Big : ByteOrder.Little)));
    Assert.That(image.Sections, Is.Empty);
    Assert.That(image.Symbols, Is.Empty);
    Assert.That(image.Relocations, Is.Empty);
    Assert.That(image.Imports, Is.Empty);
    Assert.That(image.Entries, Is.Empty);
  }

  [TestCase(false, 0, "magic")]
  [TestCase(false, 4, "4 of 16 bytes")]
  [TestCase(false, 51, "51 of 52 bytes")]
  [TestCase(true, 63, "63 of 64 bytes")]
  public void RefusesMissingOrTruncatedHeaders(bool wide, int length, string fragment)
    => Refuses(Header(wide)[..length], "PLY4060", fragment);

  [TestCase(4, 3, "class")]
  [TestCase(5, 3, "encoding")]
  [TestCase(16, 4, "type")]
  [TestCase(18, 8, "machine")]
  public void RefusesUnsupportedHeaderFields(int offset, byte value, string field) {
    var data = Header();
    data[offset] = value;
    Refuses(data, "PLY4061", field);
  }

  [TestCase(false)]
  [TestCase(true)]
  public void ASectionMayEndExactlyAtTheEndOfTheFile(bool wide) {
    var data = ThreeSections(wide);
    using var scratch = new BinaryTesting();
    var path = scratch.Write("bounded.elf", data);
    var result = ImageBuilder.Load(path, null);
    Assert.That(result.Problems, Is.Empty);
    var image = result.Value!.Image;
    var code = image.Sections.Single(section => section.Name == ".text");
    Assert.That((code.Address, code.Size, code.Writable, code.Executable), Is.EqualTo((0x4000UL, 4UL, false, true)));
    Assert.That(code.Bytes.ToArray(), Is.EqualTo(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }));
    Assert.That(image.Entries, Is.EqualTo(new ulong[] { 0x4000 }));
    Assert.That(image.ByteAt(0), Is.Null, "section table and names do not map at zero");
    Assert.That(result.Value.IsDecodable(0x4003), Is.True);
  }

  [TestCase(false)]
  [TestCase(true)]
  public void ASectionMayNotExtendPastTheEndOfTheFile(bool wide) {
    var data = ThreeSections(wide);
    Refuses(data[..^1], "PLY4062", "section");
  }

  [Test]
  public void AnExtendedSectionCountCannotPointPastTheFile() {
    var data = ThreeSections(wide: true);
    Put16(data, 60, 0);
    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(64 + 32), uint.MaxValue - 1);
    Refuses(data, "PLY4062", "section");
  }

  [Test]
  public void ASectionNameTableMustExist() {
    var data = ThreeSections(wide: true);
    Put16(data, 62, 3);
    Refuses(data, "PLY4062", "string");
  }

  [TestCase(4, "version")]
  [TestCase(6, "size")]
  public void RejectsAHeaderWhoseMetadataCannotDescribeItsClass(int value, string detail) {
    var data = Header();
    if (detail == "version")
      data[6] = (byte)value;
    else
      Put16(data, 40, value);
    Refuses(data, "PLY4062", detail);
  }

  [Test]
  public void ExtendedStringTableIndexRequiresSectionHeaders() {
    var data = Header(wide: true);
    Put16(data, 62, ushort.MaxValue);
    Refuses(data, "PLY4062", "string table");
  }

  [Test]
  public void SectionNamesMustPointIntoTheirStringTable() {
    var data = ThreeSections(wide: true);
    Put32(data, 64 + 2 * 64, 17);
    Refuses(data, "PLY4062", "section name");
  }

  [Test]
  public void SectionStringTableMustEndWithANullByte() {
    var data = ThreeSections(wide: true);
    data[^5] = (byte)'x';
    Refuses(data, "PLY4062", "string table");
  }

  [TestCase(31)]
  [TestCase(33)]
  public void RejectsUnexpectedProgramHeaderWidths(int width) {
    var data = Header(length: 84);
    Put32(data, 28, 52);
    Put16(data, 42, width);
    Put16(data, 44, 1);
    Refuses(data, "PLY4062", "program headers");
  }

  [Test]
  public void RejectsATruncatedProgramHeaderTable() {
    var data = Header(length: 83);
    Put32(data, 28, 52);
    Put16(data, 42, 32);
    Put16(data, 44, 1);
    Refuses(data, "PLY4062", "program headers");
  }

  [Test]
  public void RejectsALoadSegmentWithTooLittleMemory() {
    var data = Header(length: 84);
    Put32(data, 28, 52);
    Put16(data, 42, 32);
    Put16(data, 44, 1);
    Put32(data, 52, 1);
    Put32(data, 52 + 16, 4);
    Put32(data, 52 + 20, 3);
    Refuses(data, "PLY4062", "load segment 0");
  }

  [Test]
  public void AllowsTheHighestAddressToBelongToASection() {
    var data = ThreeSections(wide: true);
    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(64 + 2 * 64 + 16), ulong.MaxValue - 3);
    var image = Read(data).Value!;
    Assert.That(image.ByteAt(ulong.MaxValue), Is.EqualTo((byte)0xDD));
  }

  [Test]
  public void WrongMagicRemainsUnknownToTheRegistry() {
    var image = new byte[] { 0x7F, 0x45, 0x4C, 0x47 };
    var registry = BinaryCodecRegistry.CreateDefault();
    Assert.That(registry.Find("elf")!.CanRead(image), Is.False);
    Assert.That(registry.Select(image, null, "wrong.elf").Problems.Single().Code, Is.EqualTo("PLY4059"));
  }

  private static BinaryResult<BinaryImage> Read(byte[] data) {
    var codec = BinaryCodecRegistry.CreateDefault().Find("elf");
    Assert.That(codec, Is.Not.Null, "default ELF codec");
    return codec!.Read(data, null);
  }

  private static void Refuses(byte[] data, string code, string fragment) {
    var result = Read(data);
    Assert.That(result.Succeeded, Is.False);
    var problem = result.Problems.Single();
    Assert.That(problem.Code, Is.EqualTo(code));
    Assert.That(problem.Message, Does.Contain(fragment));
  }

  private static byte[] Header(bool wide = false, bool big = false, int length = 0) {
    var size = wide ? 64 : 52;
    var data = new byte[Math.Max(size, length)];
    "\u007fELF"u8.CopyTo(data);
    data[4] = wide ? (byte)2 : (byte)1;
    data[5] = big ? (byte)2 : (byte)1;
    data[6] = 1;
    var machine = wide ? (big ? 183 : 62) : (big ? 40 : 3);
    Put16(data, 16, 2, big);
    Put16(data, 18, machine, big);
    Put32(data, 20, 1, big);
    Put16(data, wide ? 52 : 40, size, big);
    return data;
  }

  private static byte[] ThreeSections(bool wide) {
    var header = wide ? 64 : 52;
    var entrySize = wide ? 64 : 40;
    var names = "\0.shstrtab\0.text\0"u8;
    var nameOffset = header + 3 * entrySize;
    var codeOffset = nameOffset + names.Length;
    var data = Header(wide, length: codeOffset + 4);
    PutAddress(data, 24, 0x4000, wide);
    PutAddress(data, wide ? 40 : 32, header, wide);
    Put16(data, wide ? 58 : 46, entrySize);
    Put16(data, wide ? 60 : 48, 3);
    Put16(data, wide ? 62 : 50, 1);
    var namesHeader = header + entrySize;
    var codeHeader = namesHeader + entrySize;
    Put32(data, namesHeader, 1);
    Put32(data, namesHeader + 4, 3);
    PutAddress(data, namesHeader + (wide ? 24 : 16), nameOffset, wide);
    PutAddress(data, namesHeader + (wide ? 32 : 20), names.Length, wide);
    Put32(data, codeHeader, 11);
    Put32(data, codeHeader + 4, 1);
    PutAddress(data, codeHeader + 8, 6, wide);
    PutAddress(data, codeHeader + (wide ? 16 : 12), 0x4000, wide);
    PutAddress(data, codeHeader + (wide ? 24 : 16), codeOffset, wide);
    PutAddress(data, codeHeader + (wide ? 32 : 20), 4, wide);
    names.CopyTo(data.AsSpan(nameOffset));
    new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }.CopyTo(data, codeOffset);
    return data;
  }

  private static void PutAddress(byte[] data, int offset, int value, bool wide) {
    if (wide)
      BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), (ulong)value);
    else
      Put32(data, offset, value);
  }

  private static void Put16(byte[] data, int offset, int value, bool big = false) {
    if (big)
      BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), (ushort)value);
    else
      BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), (ushort)value);
  }

  private static void Put32(byte[] data, int offset, int value, bool big = false) {
    if (big)
      BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), (uint)value);
    else
      BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), (uint)value);
  }

}
