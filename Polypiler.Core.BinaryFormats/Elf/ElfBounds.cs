using System;
using System.Buffers.Binary;

namespace Polypiler.Core.BinaryFormats.Elf;

/// <summary>Checks ELF extents before LibObjectFile creates a table or seeks to file data.</summary>
internal readonly ref struct ElfBounds(ReadOnlySpan<byte> bytes, bool wide, bool big) {

  internal const uint NullSection = 0;
  internal const uint SymbolTable = 2;
  internal const uint StringTable = 3;
  internal const uint RelaTable = 4;
  internal const uint NoBits = 8;
  internal const uint RelTable = 9;
  internal const uint DynamicSymbols = 11;
  internal const uint ExtendedSymbolIndices = 18;

  private readonly ReadOnlySpan<byte> _bytes = bytes;

  private int HeaderSize => wide ? 64 : 52;
  private int SectionSize => wide ? 64 : 40;
  private int ProgramSize => wide ? 56 : 32;

  internal ushort Type => this.U16(16);
  internal ushort Machine => this.U16(18);

  internal void Validate() {
    if (this._bytes[6] != 1 || this.U32(20) != 1)
      throw new ElfCorruption("ELF header has an invalid version");
    if (this.U16(wide ? 52 : 40) != this.HeaderSize)
      throw new ElfCorruption("ELF header size does not match its class");

    var sections = this.Sections();
    var names = this.SectionNames(sections);
    for (var index = 1; index < sections.Count; ++index)
      this.ValidateSection(sections, index, names);

    var programs = this.U16(wide ? 56 : 44);
    if (programs == ushort.MaxValue)
      throw new ElfCorruption("extended program-header counts are not supported");
    var programHeaders = this.Table("program headers", this.Word(wide ? 32 : 28), this.U16(wide ? 54 : 42), this.ProgramSize, programs);
    for (var index = 0; index < programHeaders.Count; ++index)
      this.ValidateProgram(programHeaders.At(index), index);
  }

  private HeaderTable Sections() {
    var offset = this.Word(wide ? 40 : 32);
    var count = this.U16(wide ? 60 : 48);
    var entry = this.U16(wide ? 58 : 46);
    if (offset == 0 && count == 0)
      return default;
    var first = this.Table("section headers", offset, entry, this.SectionSize, 1).Start;
    if (this.U32(first + 4) != NullSection)
      throw new ElfCorruption("section header 0 is not SHT_NULL");
    var actual = count == 0 ? this.Word(first + (wide ? 32 : 20)) : count;
    if (actual == 0)
      throw new ElfCorruption("section header count is zero despite a section table offset");
    return this.Table("section headers", offset, entry, this.SectionSize, actual);
  }

  private ReadOnlySpan<byte> SectionNames(HeaderTable sections) {
    var index = (uint)this.U16(wide ? 62 : 50);
    if (index == 0)
      return [];
    if (sections.Count == 0)
      throw new ElfCorruption("section string table index needs a section header table");
    if (index == ushort.MaxValue)
      index = this.U32(sections.Start + (wide ? 40 : 24));
    if (index == 0 || index >= (uint)sections.Count || this.U32(sections.At((int)index) + 4) != StringTable)
      throw new ElfCorruption($"section string table index {index} does not name a string table");
    var header = sections.At((int)index);
    var offset = this.Word(header + (wide ? 24 : 16));
    var size = this.Word(header + (wide ? 32 : 20));
    this.Extent("section string table", offset, size);
    var names = this._bytes.Slice((int)offset, (int)size);
    if (!names.IsEmpty && (names[0] != 0 || names[^1] != 0))
      throw new ElfCorruption("section string table must begin and end with a null byte");
    return names;
  }

  private void ValidateSection(HeaderTable sections, int index, ReadOnlySpan<byte> names) {
    var header = sections.At(index);
    var name = this.U32(header);
    if (name != 0 && name >= (uint)names.Length)
      throw new ElfCorruption($"section name index {name} for section {index} is outside the string table");
    var type = this.U32(header + 4);
    var address = this.Word(header + (wide ? 16 : 12));
    var offset = this.Word(header + (wide ? 24 : 16));
    var size = this.Word(header + (wide ? 32 : 20));
    if (type != NoBits)
      this.Extent($"section {index}", offset, size);
    if (this.CrossesAddressSpace(address, size))
      throw new ElfCorruption($"section {index} overflows its virtual address range");

    var (width, linkedType) = type switch {
      SymbolTable or DynamicSymbols => (wide ? 24 : 16, StringTable),
      RelaTable => (wide ? 24 : 12, SymbolTable),
      RelTable => (wide ? 16 : 8, SymbolTable),
      ExtendedSymbolIndices => (4, SymbolTable),
      _ => (0, 0U),
    };
    if (width == 0)
      return;

    var entry = this.Word(header + (wide ? 56 : 36));
    if (entry != (ulong)width || size % (ulong)width != 0)
      throw new ElfCorruption($"section {index} has size {size} and entry size {entry}, expected whole {width}-byte entries");

    var link = this.U32(header + (wide ? 40 : 24));
    if (link == 0 || link >= (uint)sections.Count)
      throw new ElfCorruption($"section {index} links to missing section {link}");
    var actual = this.U32(sections.At((int)link) + 4);
    if (actual != linkedType && (linkedType != SymbolTable || actual != DynamicSymbols))
      throw new ElfCorruption($"section {index} links to section {link} of type {actual}, expected {linkedType}");
    if (type is RelTable or RelaTable) {
      var target = this.U32(header + (wide ? 44 : 28));
      if (target >= (uint)sections.Count)
        throw new ElfCorruption($"relocation section {index} names missing target section {target}");
    }
  }

  private void ValidateProgram(int header, int index) {
    var offset = this.Word(header + (wide ? 8 : 4));
    var fileSize = this.Word(header + (wide ? 32 : 16));
    this.Extent($"program segment {index}", offset, fileSize);
    if (this.U32(header) != 1)
      return;
    var memory = this.Word(header + (wide ? 40 : 20));
    var address = this.Word(header + (wide ? 16 : 8));
    if (fileSize > memory || this.CrossesAddressSpace(address, memory))
      throw new ElfCorruption($"load segment {index} has invalid virtual address, file size or memory size");
  }

  private bool CrossesAddressSpace(ulong address, ulong size)
    => size != 0 && size - 1 > (wide ? ulong.MaxValue : uint.MaxValue) - address;

  private HeaderTable Table(string name, ulong offset, int entry, int expected, ulong count) {
    if (offset == 0 && count == 0)
      return default;
    if (offset < (ulong)this.HeaderSize || entry != expected || count == 0)
      throw new ElfCorruption($"{name} offset {offset}, count {count} or entry size {entry} is invalid (expected {expected}-byte entries)");
    if (count > ulong.MaxValue / (ulong)entry)
      throw new ElfCorruption($"{name} size overflows for {count} entries of {entry} bytes");
    this.Extent(name, offset, count * (ulong)entry);
    return new((int)offset, entry, (int)count);
  }

  private void Extent(string name, ulong offset, ulong size) {
    if (offset > (ulong)this._bytes.Length || size > (ulong)this._bytes.Length - offset)
      throw new ElfCorruption($"{name} has offset {offset} and size {size} outside file size {this._bytes.Length}");
  }

  private ushort U16(int offset) => big ? BinaryPrimitives.ReadUInt16BigEndian(this._bytes[offset..]) : BinaryPrimitives.ReadUInt16LittleEndian(this._bytes[offset..]);
  private uint U32(int offset) => big ? BinaryPrimitives.ReadUInt32BigEndian(this._bytes[offset..]) : BinaryPrimitives.ReadUInt32LittleEndian(this._bytes[offset..]);
  private ulong Word(int offset) => wide
    ? (big ? BinaryPrimitives.ReadUInt64BigEndian(this._bytes[offset..]) : BinaryPrimitives.ReadUInt64LittleEndian(this._bytes[offset..]))
    : this.U32(offset);

  private readonly record struct HeaderTable(int Start, int Width, int Count) {

    internal int At(int index) => this.Start + this.Width * index;

  }

}

internal sealed class ElfCorruption(string detail) : Exception(detail);
