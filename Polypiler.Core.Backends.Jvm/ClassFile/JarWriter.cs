using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Backends.Jvm.ClassFile;

/// <summary>
/// Packages JVM <c>.class</c> binaries into a deterministic JAR archive: <c>META-INF/MANIFEST.MF</c>
/// first, remaining entries in ordinal sorted order, and a fixed ZIP timestamp (1980-01-01 00:00:00 UTC)
/// so identical IR produces byte-for-byte identical JARs.
/// </summary>
internal static class JarWriter {

  private static readonly DateTimeOffset _fixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
  private static readonly UTF8Encoding _utf8 = new(false);

  /// <summary>Creates a deterministic JAR byte array from the given class entries and optional <c>Main-Class</c>.</summary>
  /// <param name="entries">Pairs of JAR entry path (e.g. <c>pkg/Foo.class</c>) and binary content.</param>
  /// <param name="mainClass">Optional dotted Java main class name for <c>META-INF/MANIFEST.MF</c>.</param>
  /// <returns>The complete JAR archive bytes.</returns>
  public static byte[] Write(IReadOnlyList<(string Path, byte[] Bytes)> entries, string? mainClass) {
    using var stream = new MemoryStream();
    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true)) {
      var manifest = new StringBuilder("Manifest-Version: 1.0\r\n");
      if (!string.IsNullOrEmpty(mainClass))
        manifest.Append("Main-Class: ").Append(mainClass).Append("\r\n");
      manifest.Append("\r\n");

      AddEntry(archive, "META-INF/MANIFEST.MF", _utf8.GetBytes(manifest.ToString()));
      foreach (var (path, bytes) in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
        AddEntry(archive, path, bytes);
    }

    return stream.ToArray();
  }

  private static void AddEntry(ZipArchive archive, string path, byte[] bytes) {
    var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
    entry.LastWriteTime = _fixedTimestamp;
    entry.ExternalAttributes = 0;
    using var entryStream = entry.Open();
    entryStream.Write(bytes, 0, bytes.Length);
  }

}
