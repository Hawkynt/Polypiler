using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Verification.Rebuild;

/// <summary>One file of a tree as the rebuild check compares it.</summary>
/// <param name="Path">The path relative to the tree's root, with <c>/</c> between segments.</param>
/// <param name="Sha256">The SHA-256 of <paramref name="Content"/>, in lower-case hexadecimal.</param>
/// <param name="Content">
/// The bytes compared: the file's own bytes, or for a manifest that is JSON its canonical form after the
/// ignored pointers were removed (<see cref="RebuildIgnore.Canonicalize"/>).
/// </param>
public sealed record FileDigest(string Path, string Sha256, ReadOnlyMemory<byte> Content);

/// <summary>A tree: the hash of its listing and its files, sorted by path (ordinal).</summary>
/// <param name="Sha256">The SHA-256 of <see cref="Listing"/> in UTF-8, in lower-case hexadecimal.</param>
/// <param name="Files">The files, sorted by path (ordinal).</param>
public sealed record TreeDigest(string Sha256, IReadOnlyList<FileDigest> Files) {

  /// <summary>
  /// The listing the tree hash is taken of: one line per file, <c>&lt;sha256&gt;</c>, two spaces and the
  /// path, ending in <c>\n</c>; the format <c>sha256sum</c> prints, so the hash can be recomputed with it.
  /// </summary>
  public string Listing => ListingOf(this.Files);

  /// <summary>The listing of files, in the order given.</summary>
  /// <param name="files">The files.</param>
  public static string ListingOf(IEnumerable<FileDigest> files)
    => files.Aggregate(new StringBuilder(), (text, file) => text.Append(file.Sha256).Append("  ").Append(file.Path).Append('\n')).ToString();

}
