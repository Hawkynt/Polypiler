using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Polypiler.Core.Verification.Manifests;

namespace Polypiler.Core.Verification.Rebuild;

/// <summary>
/// Hashes an output tree for the rebuild check: every file that is not ignored, its bytes as they are
/// (no newline or encoding normalization), a manifest that is JSON in its canonical form without the
/// ignored pointers; the tree hash is the SHA-256 of the <c>sha256sum</c>-style listing sorted by path.
/// Empty directories do not contribute.
/// </summary>
public static class TreeHash {

  /// <summary>Hashes the files below a directory.</summary>
  /// <param name="directory">The tree's root.</param>
  /// <param name="ignore">What is left out or normalized.</param>
  /// <returns>The digest.</returns>
  /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
  public static TreeDigest Compute(string directory, RebuildIgnore ignore) {
    ArgumentNullException.ThrowIfNull(directory);
    if (!Directory.Exists(directory))
      throw new DirectoryNotFoundException($"'{directory}' does not exist");

    return Compute(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
      .ToDictionary(file => Path.GetRelativePath(directory, file).Replace('\\', '/'), file => (ReadOnlyMemory<byte>)File.ReadAllBytes(file), StringComparer.Ordinal), ignore);
  }

  /// <summary>Hashes files given by their path relative to the tree's root.</summary>
  /// <param name="files">The files, by path with <c>/</c> or <c>\</c> between segments.</param>
  /// <param name="ignore">What is left out or normalized.</param>
  /// <returns>The digest.</returns>
  public static TreeDigest Compute(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, RebuildIgnore ignore) {
    ArgumentNullException.ThrowIfNull(files);
    ArgumentNullException.ThrowIfNull(ignore);
    var digests = files
      .Select(file => (Path: file.Key.Replace('\\', '/'), Content: file.Value))
      .Where(file => !ignore.IsIgnored(file.Path))
      .OrderBy(file => file.Path, StringComparer.Ordinal)
      .Select(file => {
        var content = IsManifest(file.Path) ? ignore.Canonicalize(file.Content) ?? file.Content : file.Content;
        return new FileDigest(file.Path, Hex(content.Span), content);
      })
      .ToArray();
    return new(Hex(Encoding.UTF8.GetBytes(TreeDigest.ListingOf(digests))), digests);
  }

  /// <summary>Whether a path names an output manifest, which is compared as JSON.</summary>
  /// <param name="path">The path, with <c>/</c> between segments.</param>
  public static bool IsManifest(string path) => path.Split('/')[^1] == Manifest.FileName;

  private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

}
