using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using LibObjectFile;
using LibObjectFile.Elf;
using Polypiler.Core.MachineSemantics;

namespace Polypiler.Core.BinaryFormats.Elf;

/// <summary>Reads ELF32 and ELF64 objects, executables and shared images for the supported machines.</summary>
public sealed class ElfCodec : IBinaryCodec {

  private static ReadOnlySpan<byte> Magic => "\u007FELF"u8;

  /// <inheritdoc/>
  public string Id => "elf";

  /// <inheritdoc/>
  public bool CanRead(ReadOnlySpan<byte> prefix) => prefix.StartsWith(Magic);

  /// <inheritdoc/>
  public BinaryResult<BinaryImage> Read(ReadOnlyMemory<byte> file, BinaryManifest? manifest)
    => this.Read(file, manifest, manifest?.ResolvedImagePath ?? "ELF input");

  /// <inheritdoc/>
  public BinaryResult<BinaryImage> Read(ReadOnlyMemory<byte> file, BinaryManifest? manifest, string fileName) {
    var bytes = file.Span;
    if (!this.CanRead(bytes))
      return Incomplete(fileName, "invalid ELF magic");
    if (bytes.Length < 16)
      return Incomplete(fileName, $"header truncated at {bytes.Length} of 16 bytes");
    if (bytes[4] is not (1 or 2))
      return Unsupported("class", bytes[4]);
    if (bytes[5] is not (1 or 2))
      return Unsupported("encoding", bytes[5]);

    var wide = bytes[4] == 2;
    var big = bytes[5] == 2;
    var headerSize = wide ? 64 : 52;
    if (bytes.Length < headerSize)
      return Incomplete(fileName, $"header truncated at {bytes.Length} of {headerSize} bytes");

    var bounds = new ElfBounds(bytes, wide, big);
    var (isa, mode) = (bounds.Machine, wide) switch {
      (3, false) => ("x86", "protected32"),
      (62, true) => ("x64", "long64"),
      (40, false) => ("arm32", "arm"),
      (183, true) => ("aarch64", "a64"),
      _ => (null, null)
    };
    if (isa is null)
      return Unsupported("machine for this class", bounds.Machine);
    var type = bounds.Type switch {
      1 => "ET_REL",
      2 => "ET_EXEC",
      3 => "ET_DYN",
      _ => null
    };
    if (type is null)
      return Unsupported("type", bounds.Type);

    try {
      bounds.Validate();
      // LibObjectFile owns ELF structure parsing. The preflight caps all table extents before it reads them.
      using var stream = new MemoryStream(file.ToArray(), writable: false);
      var options = new ElfReaderOptions { UseSubStream = true, TryCreateSection = ReadSection };
      if (!ElfFile.TryRead(stream, out var elf, out var problems, options))
        return Corrupt(problems.ToString());
      return this.Project(file, elf, isa, mode!, type, big);
    } catch (Exception exception) when (exception is ElfCorruption or ObjectFileException or IOException or
                                         ArgumentException or InvalidOperationException or OverflowException or IndexOutOfRangeException) {
      return Corrupt(exception.Message);
    }
  }

  // Notes have no role in recovery. Treat them as bytes, including vendor-defined note payloads.
  private static ElfSection ReadSection(ElfSectionType type, LibObjectFile.Diagnostics.DiagnosticBag _)
    => type == ElfSectionType.Note ? new ElfStreamSection(type) : null!;

  private BinaryResult<BinaryImage> Project(ReadOnlyMemory<byte> file, ElfFile elf, string isa, string mode, string type, bool big) {
    var executableSections = elf.Sections.Where(section => section.Type != ElfSectionType.Null).ToArray();
    var sections = executableSections.Length == 0
      ? elf.Segments.Where(segment => segment.Type == ElfSegmentTypeCore.Load)
        .Select(segment => LoadSegment(file, segment)).ToImmutableArray()
      : executableSections.Select(section => Section(file, section)).ToImmutableArray();
    var tableEntries = elf.Sections.OfType<ElfSymbolTable>().SelectMany(table => table.Entries).ToArray();
    var symbols = tableEntries.Where(symbol => !symbol.SectionLink.IsEmpty && !symbol.Name.IsEmpty
        && symbol.Type is not (ElfSymbolType.Tls or ElfSymbolType.GnuIndirectFunction))
      .Select(symbol => Symbol(symbol, isa == "arm32")).Distinct().ToImmutableArray();

    var relocations = ImmutableArray.CreateBuilder<ImageRelocation>();
    var imports = ImmutableArray.CreateBuilder<ImageImport>();
    var warnings = ImmutableArray.CreateBuilder<Diagnostic>();
    foreach (var table in elf.Sections.OfType<ElfRelocationTable>()) {
      if (table.Link.Section is not ElfSymbolTable symbolTable)
        throw new ElfCorruption($"relocation section '{table.Name.Value}' has no symbol table");
      var target = table.Info.Section;
      foreach (var relocation in table.Entries) {
        if (relocation.SymbolIndex >= symbolTable.Entries.Count)
          throw new ElfCorruption($"relocation at {BinaryDiagnostics.Hex(relocation.Offset)} has out-of-range symbol index {relocation.SymbolIndex}");
        var source = symbolTable.Entries[(int)relocation.SymbolIndex];
        var name = SymbolName(source);
        var kind = ElfRelocationTables.Kind(isa, relocation.Type.Value);
        if (source.Type is ElfSymbolType.Tls or ElfSymbolType.GnuIndirectFunction
            || RequiresUnsupportedSection(source.SectionLink.Section) || RequiresUnsupportedSection(target)
            || IsVersioned(file, elf, symbolTable, relocation.SymbolIndex))
          kind = RelocationKind.Unsupported;

        var address = relocation.Offset;
        var addend = table.IsRelocationWithAddends ? relocation.Addend
          : ElfRelocationTables.ImplicitAddend(file, elf, isa, kind, relocation.Type.Value, target, address, big);
        if (kind == RelocationKind.Unsupported)
          warnings.Add(Diagnostic.Create(ElfDiagnostics.UnsupportedRelocation, null, relocation.Type, BinaryDiagnostics.Hex(address)));
        if (kind == RelocationKind.ImportSlot && source.SectionLink.IsEmpty && name is not null)
          imports.Add(new ImageImport(address, name, null, null));
        relocations.Add(new ImageRelocation(address, kind, name, addend, unchecked((int)relocation.Type.Value)) {
          TargetSection = elf.FileType == ElfFileType.Relocatable ? target?.Name.Value : null
        });
      }
    }

    var entry = elf.EntryPointAddress;
    var modes = isa is "arm32" or "aarch64" ? ElfArmModes.Ranges(tableEntries) : [];
    var image = new BinaryImage(this.Id, isa, mode, big ? ByteOrder.Big : ByteOrder.Little, sections, symbols,
      relocations.ToImmutable(), imports.ToImmutable(), entry == 0 ? [] : [isa == "arm32" ? entry & ~1UL : entry]) {
      ObjectType = type,
      ModeRanges = modes
    };
    return BinaryResult<BinaryImage>.Success(image, warnings.ToImmutable());
  }

  private static ImageSection Section(ReadOnlyMemory<byte> file, ElfSection section) {
    var allocated = section.Flags.HasFlag(ElfSectionFlags.Alloc);
    return new ImageSection(section.Name.Value, section.VirtualAddress,
      section.HasContent ? file.Slice((int)section.Position, (int)section.Size) : ReadOnlyMemory<byte>.Empty,
      section.Size, allocated, section.Flags.HasFlag(ElfSectionFlags.Write), section.Flags.HasFlag(ElfSectionFlags.Executable)) {
      Allocated = allocated,
      ThreadLocal = section.Flags.HasFlag(ElfSectionFlags.Tls)
    };
  }

  private static ImageSection LoadSegment(ReadOnlyMemory<byte> file, ElfSegment segment) {
    var flags = (ElfSegmentFlagsCore)segment.Flags.Value;
    return new($"LOAD[{segment.Index}]", segment.VirtualAddress, file.Slice((int)segment.Position, (int)segment.Size),
      segment.SizeInMemory, flags.HasFlag(ElfSegmentFlagsCore.Readable), flags.HasFlag(ElfSegmentFlagsCore.Writable),
      flags.HasFlag(ElfSegmentFlagsCore.Executable));
  }

  private static ImageSymbol Symbol(ElfSymbol symbol, bool arm) {
    var kind = symbol.Type switch {
      ElfSymbolType.Function => SymbolKind.Function,
      ElfSymbolType.Object or ElfSymbolType.Common => SymbolKind.Data,
      ElfSymbolType.Section or ElfSymbolType.NoType => SymbolKind.Label,
      _ => SymbolKind.Unknown
    };
    var thumb = arm && kind == SymbolKind.Function && (symbol.Value & 1) != 0;
    return new(symbol.Name.Value, thumb ? symbol.Value & ~1UL : symbol.Value, symbol.Size, kind, thumb ? "thumb" : null);
  }

  private static bool RequiresUnsupportedSection(ElfSection? section)
    => section is not null && (section.Flags & (ElfSectionFlags.Tls | ElfSectionFlags.Compressed)) != 0;

  private static string? SymbolName(ElfSymbol symbol)
    => !symbol.Name.IsEmpty ? symbol.Name.Value : symbol.SectionLink.Section?.Name.Value;

  private static bool IsVersioned(ReadOnlyMemory<byte> file, ElfFile elf, ElfSymbolTable table, uint index) {
    const uint gnuVersionSymbols = 0x6FFFFFFF;
    var versions = elf.Sections.FirstOrDefault(section => (uint)section.Type == gnuVersionSymbols && section.Link.Section == table);
    if (versions is null)
      return false;
    var offset = (ulong)index * 2;
    if (versions.Size < offset + 2)
      throw new ElfCorruption($"symbol version table '{versions.Name.Value}' has no entry for symbol {index}");
    var bytes = file.Span.Slice((int)(versions.Position + offset), 2);
    return ElfRelocationTables.Read16(bytes, elf.Encoding == ElfEncoding.Msb) > 1;
  }

  private static BinaryResult<BinaryImage> Incomplete(string file, string why)
    => BinaryResult<BinaryImage>.Failure(Diagnostic.Create(ElfDiagnostics.Incomplete, null, file, why));

  private static BinaryResult<BinaryImage> Unsupported(string field, int value)
    => BinaryResult<BinaryImage>.Failure(Diagnostic.Create(ElfDiagnostics.Unsupported, null, field, value));

  private static BinaryResult<BinaryImage> Corrupt(string why)
    => BinaryResult<BinaryImage>.Failure(Diagnostic.Create(ElfDiagnostics.Corrupt, null, why));

}
