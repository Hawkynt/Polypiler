using System;
using System.Collections.Immutable;
using System.Linq;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Ports;

namespace Polypiler.Core.BinaryFormats;

/// <summary>
/// A loaded binary independent of its container: the sections with their permissions, the symbols,
/// relocations and imports the container states, the entry points, and the machine the code is for.
/// Loading never changes a byte: relocations are recorded, not applied.
/// </summary>
/// <param name="Container">The ID of the codec that read it, for example <c>raw</c> or <c>elf</c>.</param>
/// <param name="Isa">The ISA, as its machine profile names it.</param>
/// <param name="Mode">The decoding mode, for example <c>protected32</c>.</param>
/// <param name="ByteOrder">The byte order.</param>
/// <param name="Sections">The sections.</param>
/// <param name="Symbols">The symbols.</param>
/// <param name="Relocations">The relocations.</param>
/// <param name="Imports">The imports.</param>
/// <param name="Entries">The entry points.</param>
public sealed record BinaryImage(string Container, string Isa, string Mode, ByteOrder ByteOrder, ImmutableArray<ImageSection> Sections, ImmutableArray<ImageSymbol> Symbols, ImmutableArray<ImageRelocation> Relocations, ImmutableArray<ImageImport> Imports, ImmutableArray<ulong> Entries) : IMachineCodeSource {

  /// <summary>The container's object type, when provided.</summary>
  public string? ObjectType { get; init; }

  /// <summary>Ranges whose instruction set or data state differs from adjacent ranges.</summary>
  public ImmutableArray<ImageModeRange> ModeRanges { get; init; } = [];

  /// <summary>The section that holds an address, or <see langword="null"/>.</summary>
  /// <param name="address">The address.</param>
  /// <returns>The section.</returns>
  public ImageSection? SectionAt(ulong address) => this.Sections.FirstOrDefault(section => section.IsMapped && section.Contains(address));

  /// <summary>The mode of the mapped section at this address, including <c>data</c>.</summary>
  /// <param name="address">The address.</param>
  /// <returns>The specific range mode, or the image's default mode.</returns>
  public string ModeAt(ulong address) {
    var section = this.SectionAt(address);
    return this.ModeRanges.FirstOrDefault(range => range.Section == section?.Name && address >= range.Address
      && address - range.Address < range.Size)?.Mode ?? this.Mode;
  }

  /// <inheritdoc/>
  public byte? ByteAt(ulong address) => this.SectionAt(address)?.ByteAt(address);

  /// <inheritdoc/>
  public bool Equals(BinaryImage? other)
    => other is not null && this.Container == other.Container && this.Isa == other.Isa && this.Mode == other.Mode && this.ByteOrder == other.ByteOrder
      && this.Sections.SequenceEqual(other.Sections) && this.Symbols.SequenceEqual(other.Symbols) && this.Relocations.SequenceEqual(other.Relocations)
      && this.Imports.SequenceEqual(other.Imports) && this.Entries.SequenceEqual(other.Entries) && this.ObjectType == other.ObjectType
      && this.ModeRanges.SequenceEqual(other.ModeRanges);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Container, this.Isa, this.Mode, this.ByteOrder, this.Sections.Length);

}

/// <summary>A section-relative instruction-set or data range.</summary>
/// <param name="Address">The range's first address.</param>
/// <param name="Size">The range size in bytes.</param>
/// <param name="Mode">The mode name, or <c>data</c>.</param>
/// <param name="Section">The containing section name.</param>
public sealed record ImageModeRange(ulong Address, ulong Size, string Mode, string Section);

/// <summary>
/// A section of an image: its bytes at an address, and how much address space it takes, which may be
/// more than its bytes (the rest reads as zero, as a loader fills it).
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="Address">The address of the first byte.</param>
/// <param name="Bytes">The bytes the file holds.</param>
/// <param name="VirtualSize">The size in memory, at least the number of bytes.</param>
/// <param name="Readable">Whether it may be read.</param>
/// <param name="Writable">Whether it may be written.</param>
/// <param name="Executable">Whether code runs from it.</param>
public sealed record ImageSection(string Name, ulong Address, ReadOnlyMemory<byte> Bytes, ulong VirtualSize, bool Readable, bool Writable, bool Executable) {

  /// <summary>Whether the section occupies address space at run time.</summary>
  public bool Allocated { get; init; } = true;

  /// <summary>Whether the section belongs to thread-local storage.</summary>
  public bool ThreadLocal { get; init; }

  /// <summary>Whether the section is mapped in the ordinary process address space.</summary>
  public bool IsMapped => this.Allocated && !this.ThreadLocal;

  /// <summary>The size in memory: the virtual size or the number of bytes, whichever is larger.</summary>
  public ulong Size => Math.Max(this.VirtualSize, (ulong)this.Bytes.Length);

  /// <summary>Whether an address lies in the section.</summary>
  /// <param name="address">The address.</param>
  /// <returns>Whether it does.</returns>
  public bool Contains(ulong address) => address >= this.Address && address - this.Address < this.Size;

  /// <summary>The byte at an address: the file's byte, zero past the file's bytes, <see langword="null"/> outside the section.</summary>
  /// <param name="address">The address.</param>
  /// <returns>The byte.</returns>
  public byte? ByteAt(ulong address) {
    if (!this.Contains(address))
      return null;
    var offset = address - this.Address;
    return offset < (ulong)this.Bytes.Length ? this.Bytes.Span[(int)offset] : (byte)0;
  }

  /// <inheritdoc/>
  public bool Equals(ImageSection? other)
    => other is not null && this.Name == other.Name && this.Address == other.Address && this.Bytes.Span.SequenceEqual(other.Bytes.Span)
      && this.VirtualSize == other.VirtualSize && this.Allocated == other.Allocated && this.ThreadLocal == other.ThreadLocal
      && this.Readable == other.Readable && this.Writable == other.Writable && this.Executable == other.Executable;

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Name, this.Address, this.Bytes.Length, this.VirtualSize);

}

/// <summary>What a symbol names.</summary>
public enum SymbolKind {

  /// <summary>A routine.</summary>
  Function,

  /// <summary>Data.</summary>
  Data,

  /// <summary>A position inside code or data.</summary>
  Label,

  /// <summary>The container does not say.</summary>
  Unknown

}

/// <summary>A named address the container states.</summary>
/// <param name="Name">The name.</param>
/// <param name="Address">The address.</param>
/// <param name="Size">The size in bytes, 0 when unknown.</param>
/// <param name="Kind">What it names.</param>
/// <param name="Mode">The decoding mode of the code there when it differs from the image's (ARM/Thumb), or <see langword="null"/>.</param>
public sealed record ImageSymbol(string Name, ulong Address, ulong Size, SymbolKind Kind, string? Mode);

/// <summary>How a relocation computes the value it patches.</summary>
public enum RelocationKind {

  /// <summary>The address of the symbol plus the addend.</summary>
  Absolute,

  /// <summary>The distance from the patched place to the symbol plus the addend.</summary>
  PcRelative,

  /// <summary>The slot an import is bound to.</summary>
  ImportSlot,

  /// <summary>The difference between the actual and the preferred load address.</summary>
  BaseRelative,

  /// <summary>A type the codec records but does not model; <see cref="ImageRelocation.RawType"/> keeps it.</summary>
  Unsupported

}

/// <summary>A place the loader patches; recorded, never applied to the bytes.</summary>
/// <param name="Address">The patched address.</param>
/// <param name="Kind">How the value is computed.</param>
/// <param name="Symbol">The symbol, or <see langword="null"/>.</param>
/// <param name="Addend">The addend.</param>
/// <param name="RawType">The container's own relocation type.</param>
public sealed record ImageRelocation(ulong Address, RelocationKind Kind, string? Symbol, long Addend, int RawType) {

  /// <summary>The target section for a section-relative relocation, when provided.</summary>
  public string? TargetSection { get; init; }

}

/// <summary>A slot the loader binds to a symbol of another module.</summary>
/// <param name="SlotAddress">The address of the slot.</param>
/// <param name="Symbol">The imported symbol.</param>
/// <param name="Library">The library, or <see langword="null"/> when the container does not name one.</param>
/// <param name="Ordinal">The ordinal, or <see langword="null"/>.</param>
public sealed record ImageImport(ulong SlotAddress, string Symbol, string? Library, int? Ordinal);
