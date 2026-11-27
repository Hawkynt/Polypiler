using System;

namespace Polypiler.Core.BinaryFormats;

/// <summary>Reads one container format into a <see cref="BinaryImage"/>.</summary>
public interface IBinaryCodec {

  /// <summary>The container's ID, as the manifest field <c>container</c> names it: <c>raw</c>, <c>elf</c>, <c>pe</c>.</summary>
  string Id { get; }

  /// <summary>
  /// Whether the codec can identify the container from the supplied prefix. Codecs for the DOS
  /// family are selected by ID once the registry identifies their signatures.
  /// </summary>
  /// <param name="prefix">The first bytes of the file, as many as there are up to 64.</param>
  /// <returns>Whether it is.</returns>
  bool CanRead(ReadOnlySpan<byte> prefix);

  /// <summary>Reads a file.</summary>
  /// <param name="file">The whole file.</param>
  /// <param name="manifest">The manifest, or <see langword="null"/>.</param>
  /// <returns>The image, or why there is none.</returns>
  BinaryResult<BinaryImage> Read(ReadOnlyMemory<byte> file, BinaryManifest? manifest);

  /// <summary>Reads a file with its name available for diagnostics.</summary>
  /// <param name="file">The whole file.</param>
  /// <param name="manifest">The manifest, or <see langword="null"/>.</param>
  /// <param name="fileName">The file name as passed by the caller.</param>
  /// <returns>The image, or why there is none.</returns>
  BinaryResult<BinaryImage> Read(ReadOnlyMemory<byte> file, BinaryManifest? manifest, string fileName) => this.Read(file, manifest);

}
