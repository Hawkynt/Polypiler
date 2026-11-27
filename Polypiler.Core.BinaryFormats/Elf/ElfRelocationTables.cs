using System;
using System.Buffers.Binary;
using System.Linq;
using LibObjectFile.Elf;

namespace Polypiler.Core.BinaryFormats.Elf;

/// <summary>Relocation constants and implicit addend fields from the i386, AMD64, AAELF32 and AAELF64 psABIs.</summary>
internal static class ElfRelocationTables {

  internal static RelocationKind Kind(string isa, uint raw) => (isa, raw) switch {
    ("x86", 1) or ("x64", 1 or 10 or 11) or ("arm32", 2) or ("aarch64", 257 or 277) => RelocationKind.Absolute,
    ("x86", 2 or 4) or ("x64", 2 or 4) or ("arm32", 3 or 10 or 28 or 29 or 30)
      or ("aarch64", 275 or 282 or 283) => RelocationKind.PcRelative,
    ("x86" or "x64", 6 or 7) or ("arm32", 21 or 22) or ("aarch64", 1025 or 1026) => RelocationKind.ImportSlot,
    ("x86" or "x64", 8) or ("arm32", 23) or ("aarch64", 1027) => RelocationKind.BaseRelative,
    _ => RelocationKind.Unsupported
  };

  internal static long ImplicitAddend(ReadOnlyMemory<byte> file, ElfFile elf, string isa, RelocationKind kind,
    uint raw, ElfSection? target, ulong address, bool big) {
    if (kind == RelocationKind.Unsupported || (kind == RelocationKind.ImportSlot && isa is "x86" or "x64"))
      return 0;

    var width = (isa, raw) switch {
      ("x64", 1 or 8) or ("aarch64", 257 or 1025 or 1026 or 1027) => 8,
      _ => 4
    };
    var field = FindField(file.Span, elf, target, address, width);
    if (field.IsEmpty)
      return 0;

    // AAELF64 instructions are little endian, and AAELF32 BE8 images store instruction bytes little endian.
    const uint armBe8 = 0x00800000;
    var bigInstructions = big && (elf.Flags.Value & armBe8) == 0;
    return (isa, raw) switch {
      ("arm32", 10 or 30) => ThumbBranchAddend(field, bigInstructions),
      ("arm32", 28 or 29) => ArmBranchAddend(Read32(field, bigInstructions)),
      ("aarch64", 275) => PageAddend(Read32(field, big: false)),
      ("aarch64", 277) => (Read32(field, big: false) >> 10) & 0xFFF,
      ("aarch64", 282 or 283) => SignExtend((Read32(field, big: false) & 0x3FFFFFF) << 2, 28),
      ("x64", 10) => Read32(field, big),
      _ => width == 8 ? Read64(field, big) : unchecked((int)Read32(field, big))
    };
  }

  private static ReadOnlySpan<byte> FindField(ReadOnlySpan<byte> file, ElfFile elf, ElfSection? target, ulong address, int width) {
    var relative = elf.FileType == ElfFileType.Relocatable;
    var section = relative ? target : elf.Sections.FirstOrDefault(section =>
      address >= section.VirtualAddress && address - section.VirtualAddress < section.Size && (section.Flags & (ElfSectionFlags.Alloc | ElfSectionFlags.Tls)) == ElfSectionFlags.Alloc);
    if (section is not null) {
      var offset = relative ? address : address - section.VirtualAddress;
      EnsureExtent(address, offset, section.Size, width);
      return section.HasContent ? file.Slice((int)(section.Position + offset), width) : [];
    }
    if (!relative)
      foreach (var segment in elf.Segments) {
        if (segment.Type != ElfSegmentTypeCore.Load || address < segment.VirtualAddress || address - segment.VirtualAddress >= segment.SizeInMemory)
          continue;
        var offset = address - segment.VirtualAddress;
        EnsureExtent(address, offset, segment.SizeInMemory, width);
        if (offset >= segment.Size)
          return [];
        EnsureExtent(address, offset, segment.Size, width);
        return file.Slice((int)(segment.Position + offset), width);
      }
    throw new ElfCorruption($"relocation at {BinaryDiagnostics.Hex(address)} has no target section or load segment");
  }

  private static void EnsureExtent(ulong address, ulong offset, ulong size, int width) {
    if (offset > size || (ulong)width > size - offset)
      throw new ElfCorruption($"relocation at {BinaryDiagnostics.Hex(address)} extends beyond its target");
  }

  private static long ArmBranchAddend(uint instruction) {
    var immediate = (instruction & 0xFFFFFF) << 2;
    // BLX also takes the H bit as bit 1 of its branch displacement.
    if ((instruction & 0xFE000000) == 0xFA000000)
      immediate |= (instruction >> 23) & 2;
    return SignExtend(immediate, 26);
  }

  private static long ThumbBranchAddend(ReadOnlySpan<byte> field, bool big) {
    var upper = Read16(field, big);
    var lower = Read16(field[2..], big);
    var sign = (upper >> 10) & 1;
    var i1 = ((lower >> 13) ^ sign ^ 1) & 1;
    var i2 = ((lower >> 11) ^ sign ^ 1) & 1;
    var immediate = (sign << 24) | (i1 << 23) | (i2 << 22) | ((upper & 0x3FF) << 12) | ((lower & 0x7FF) << 1);
    return SignExtend((uint)immediate, 25);
  }

  private static long PageAddend(uint instruction) => SignExtend(((instruction >> 29) & 3) | ((instruction >> 3) & 0x1FFFFC), 21);

  private static int SignExtend(uint value, int width) => unchecked((int)(value << (32 - width))) >> (32 - width);

  internal static ushort Read16(ReadOnlySpan<byte> field, bool big)
    => big ? BinaryPrimitives.ReadUInt16BigEndian(field) : BinaryPrimitives.ReadUInt16LittleEndian(field);

  private static uint Read32(ReadOnlySpan<byte> field, bool big)
    => big ? BinaryPrimitives.ReadUInt32BigEndian(field) : BinaryPrimitives.ReadUInt32LittleEndian(field);

  private static long Read64(ReadOnlySpan<byte> field, bool big)
    => big ? BinaryPrimitives.ReadInt64BigEndian(field) : BinaryPrimitives.ReadInt64LittleEndian(field);

}
