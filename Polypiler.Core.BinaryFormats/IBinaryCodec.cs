using System;

namespace Polypiler.Core.BinaryFormats;

/// <summary>Reads one container format into a <see cref="BinaryImage"/>.</summary>
public interface IBinaryCodec {

  /// <summary>The container's ID, as the manifest field <c>container</c> names it: <c>raw</c>, <c>elf</c>, <c>pe</c>.</summary>
  string Id { get; }

  /// <summary>Whether a file starting with these bytes is of this container; a codec without a magic answers <see langword="false"/>.</summary>
  /// <param name="prefix">The first bytes of the file, as many as there are up to 64.</param>
  /// <returns>Whether it is.</returns>
  bool CanRead(ReadOnlySpan<byte> prefix);

  /// <summary>Reads a file.</summary>
  /// <param name="file">The whole file.</param>
  /// <param name="manifest">The manifest, or <see langword="null"/>.</param>
  /// <returns>The image, or why there is none.</returns>
  BinaryResult<BinaryImage> Read(ReadOnlyMemory<byte> file, BinaryManifest? manifest);

}
