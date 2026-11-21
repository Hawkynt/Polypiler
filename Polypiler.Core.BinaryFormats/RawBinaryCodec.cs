using System;
using System.Collections.Immutable;

namespace Polypiler.Core.BinaryFormats;

/// <summary>
/// The container <c>raw</c>: a blob without a header, read as one readable, executable section at the
/// manifest's <c>loadAddress</c>. A blob states nothing about itself, so the machine, the mode, the
/// byte order and the load address all come from the manifest; a missing one is refused, never guessed.
/// It claims no magic: a blob is raw only when the manifest says so.
/// </summary>
public sealed class RawBinaryCodec : IBinaryCodec {

  /// <inheritdoc/>
  public string Id => "raw";

  /// <inheritdoc/>
  public bool CanRead(ReadOnlySpan<byte> prefix) => false;

  /// <inheritdoc/>
  public BinaryResult<BinaryImage> Read(ReadOnlyMemory<byte> file, BinaryManifest? manifest) {
    if (manifest is null)
      return BinaryResult<BinaryImage>.Failure(Diagnostic.Create(BinaryDiagnostics.InvalidManifest, null, "(none)", "container 'raw' needs a manifest stating 'isa', 'mode', 'byteOrder' and 'loadAddress'"));

    var missing = manifest.ByteOrder is null ? "byteOrder" : manifest.LoadAddress is null ? "loadAddress" : null;
    if (missing is not null)
      return BinaryResult<BinaryImage>.Failure(Diagnostic.Create(BinaryDiagnostics.InvalidManifest, null, manifest.Source, $"container 'raw' needs the field '{missing}'"));

    var start = manifest.LoadAddress!.Value;
    var length = (ulong)file.Length;
    if (length > 0 && length - 1 > ulong.MaxValue - start)
      return BinaryResult<BinaryImage>.Failure(Diagnostic.Create(BinaryDiagnostics.InvalidManifest, null, manifest.Source, $"'loadAddress' {BinaryDiagnostics.Hex(start)} leaves no room for the {length} bytes of the image"));

    var section = new ImageSection("raw", start, file, length, Readable: true, Writable: false, Executable: true);
    return BinaryResult<BinaryImage>.Success(new BinaryImage(this.Id, manifest.Isa, manifest.Mode, manifest.ByteOrder!.Value, [section], [], [], [], []));
  }

}
