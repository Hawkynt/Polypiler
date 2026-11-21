using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.BinaryFormats;

/// <summary>
/// The codecs a load may use, found by ID or by the magic at the start of a file. A file whose magic
/// no codec claims is never taken for a raw blob: a known container without a codec is named
/// (<c>PLY4059</c>), and an unknown one is refused the same way.
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
    ([0xCA, 0xFE, 0xBA, 0xBE], "Mach-O universal"),
    ([0x4D, 0x5A], "PE")
  ];

  private readonly List<IBinaryCodec> _codecs = [];

  /// <summary>A registry with the codecs that need no container library: <see cref="RawBinaryCodec"/>.</summary>
  /// <returns>The registry.</returns>
  public static BinaryCodecRegistry CreateDefault() {
    var registry = new BinaryCodecRegistry();
    registry.Register(new RawBinaryCodec());
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

  /// <summary>The first codec that claims a file by its magic, or <see langword="null"/>.</summary>
  /// <param name="prefix">The first bytes of the file.</param>
  /// <returns>The codec.</returns>
  public IBinaryCodec? Detect(ReadOnlySpan<byte> prefix) {
    var head = prefix[..Math.Min(prefix.Length, PrefixLength)];
    foreach (var codec in this._codecs)
      if (codec.CanRead(head))
        return codec;
    return null;
  }

  /// <summary>The name of a container format known by its magic, whether or not a codec reads it, or <see langword="null"/>.</summary>
  /// <param name="prefix">The first bytes of the file.</param>
  /// <returns>The format, for example <c>Mach-O</c>.</returns>
  public static string? KnownFormat(ReadOnlySpan<byte> prefix) {
    foreach (var (magic, format) in _knownMagics)
      if (prefix.StartsWith(magic))
        return format;
    return null;
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
