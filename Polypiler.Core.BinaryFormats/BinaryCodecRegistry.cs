using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.BinaryFormats.Elf;

namespace Polypiler.Core.BinaryFormats;

/// <summary>
/// The codecs a load may use, found by ID or by executable signatures. The DOS family uses the
/// secondary signature in the supplied bytes to select its codec by ID. A known family with no
/// registered codec is named in <c>PLY4059</c>. Raw blobs require an explicit container choice.
/// </summary>
public sealed class BinaryCodecRegistry {

  /// <summary>How many bytes of a file <see cref="IBinaryCodec.CanRead"/> is shown.</summary>
  public const int PrefixLength = 64;

  private static readonly (byte[] Magic, string Format)[] _knownMagics = [
    ([0x7F, 0x45, 0x4C, 0x46], "ELF"),
    ([0xCF, 0xFA, 0xED, 0xFE], "Mach-O"),
    ([0xCE, 0xFA, 0xED, 0xFE], "Mach-O"),
    ([0xFE, 0xED, 0xFA, 0xCF], "Mach-O"),
    ([0xFE, 0xED, 0xFA, 0xCE], "Mach-O"),
    ([0xCA, 0xFE, 0xBA, 0xBE], "Mach-O universal")
  ];

  private readonly List<IBinaryCodec> _codecs = [];

  /// <summary>A registry with the raw and ELF codecs.</summary>
  /// <returns>The registry.</returns>
  public static BinaryCodecRegistry CreateDefault() {
    var registry = new BinaryCodecRegistry();
    registry.Register(new RawBinaryCodec());
    registry.Register(new ElfCodec());
    return registry;
  }

  /// <summary>The registered codecs, in the order of registration.</summary>
  public IReadOnlyList<IBinaryCodec> Codecs => this._codecs;

  /// <summary>Adds a codec.</summary>
  /// <param name="codec">The codec.</param>
  /// <exception cref="ArgumentException">A codec with the same ID is registered already.</exception>
  public void Register(IBinaryCodec codec) {
    ArgumentNullException.ThrowIfNull(codec);
    if (this.Find(codec.Id) is not null)
      throw new ArgumentException($"a codec for container '{codec.Id}' is registered already", nameof(codec));
    this._codecs.Add(codec);
  }

  /// <summary>The codec of a container ID, or <see langword="null"/>.</summary>
  /// <param name="id">The ID.</param>
  /// <returns>The codec.</returns>
  public IBinaryCodec? Find(string id) {
    ArgumentNullException.ThrowIfNull(id);
    return this._codecs.FirstOrDefault(codec => codec.Id == id);
  }

  /// <summary>Selects the identified DOS-family codec or the first ordinary codec that claims the prefix.</summary>
  /// <param name="prefix">The file bytes. DOS-family detection uses the secondary signature at its declared offset.</param>
  /// <returns>The codec, or <see langword="null"/> when no codec is available.</returns>
  public IBinaryCodec? Detect(ReadOnlySpan<byte> prefix) {
    if (IdentifyDosFamily(prefix) is { } family)
      return this.Find(family.ToLowerInvariant());

    var head = prefix[..Math.Min(prefix.Length, PrefixLength)];
    foreach (var codec in this._codecs)
      if (codec.CanRead(head))
        return codec;
    return null;
  }

  /// <summary>The name of a container format known by its magic, whether or not a codec reads it, or <see langword="null"/>.</summary>
  /// <param name="prefix">The file bytes or a prefix containing the format's signature.</param>
  /// <returns>The format, for example <c>Mach-O</c> or <c>MZ</c>.</returns>
  public static string? KnownFormat(ReadOnlySpan<byte> prefix) {
    if (IdentifyDosFamily(prefix) is { } family)
      return family;

    foreach (var (magic, format) in _knownMagics)
      if (prefix.StartsWith(magic))
        return format;
    return null;
  }

  private static string? IdentifyDosFamily(ReadOnlySpan<byte> file) {
    if (file.StartsWith("ZM"u8))
      return "MZ";
    if (!file.StartsWith("MZ"u8))
      return null;

    const int secondaryHeaderPointer = 0x3c;
    const int extendedHeaderLength = secondaryHeaderPointer + sizeof(uint);
    if (file.Length < extendedHeaderLength)
      return "MZ";

    var offset = BinaryPrimitives.ReadUInt32LittleEndian(file[secondaryHeaderPointer..]);
    if (offset < extendedHeaderLength || offset > (uint)(file.Length - sizeof(ushort)))
      return "MZ";

    var header = file[(int)offset..];
    if (header.StartsWith("PE\0\0"u8))
      return "PE";

    const int relocationTablePointer = 0x18;
    if (BinaryPrimitives.ReadUInt16LittleEndian(file[relocationTablePointer..]) != 0x40)
      return "MZ";
    if (header.StartsWith("NE"u8))
      return "NE";
    if (header.StartsWith("LE"u8))
      return "LE";
    return header.StartsWith("LX"u8) ? "LX" : "MZ";
  }

  /// <summary>The codec for a file: the one the manifest names, else the one its magic selects.</summary>
  /// <param name="file">The file's bytes.</param>
  /// <param name="container">The manifest's <c>container</c>, or <see langword="null"/> to detect.</param>
  /// <param name="fileName">The file, named by <c>PLY4059</c>.</param>
  /// <returns>The codec, or <c>PLY4059</c>.</returns>
  public BinaryResult<IBinaryCodec> Select(ReadOnlySpan<byte> file, string? container, string fileName) {
    ArgumentNullException.ThrowIfNull(fileName);
    if (container is not null)
      return this.Find(container) is { } named
        ? BinaryResult<IBinaryCodec>.Success(named)
        : BinaryResult<IBinaryCodec>.Failure(BinaryDiagnostics.UnknownContainer.At($"'{fileName}' is declared as container '{container}', which no registered codec reads; give another 'container' or use a raw profile"));

    if (this.Detect(file) is { } detected)
      return BinaryResult<IBinaryCodec>.Success(detected);
    return KnownFormat(file) is { } format
      ? BinaryResult<IBinaryCodec>.Failure(BinaryDiagnostics.UnknownContainer.At($"'{fileName}' is a {format} file, which no registered codec reads; give the manifest field 'container' or use a raw profile"))
      : BinaryResult<IBinaryCodec>.Failure(Diagnostic.Create(BinaryDiagnostics.UnknownContainer, null, fileName));
  }

}
