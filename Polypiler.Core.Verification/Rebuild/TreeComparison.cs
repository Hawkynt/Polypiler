using System;
using System.Collections.Generic;

namespace Polypiler.Core.Verification.Rebuild;

/// <summary>How two trees differ at one path.</summary>
public enum DifferenceKind {

  /// <summary>Both trees have the file, with different bytes.</summary>
  Content,

  /// <summary>Only the first tree has the file.</summary>
  OnlyInFirst,

  /// <summary>Only the second tree has the file.</summary>
  OnlyInSecond

}

/// <summary>One difference between two trees.</summary>
/// <param name="Kind">How they differ.</param>
/// <param name="Path">The file's path, with <c>/</c> between segments.</param>
/// <param name="Offset">
/// For <see cref="DifferenceKind.Content"/> the first byte that differs, the length of the shorter file
/// when one is a prefix of the other; <see langword="null"/> otherwise.
/// </param>
public sealed record TreeDifference(DifferenceKind Kind, string Path, long? Offset = null);

/// <summary>Compares two tree digests file by file; paths are compared case-sensitively (ordinal) on every host.</summary>
public static class TreeComparison {

  /// <summary>Every difference between two trees, in path order.</summary>
  /// <param name="first">The first tree.</param>
  /// <param name="second">The tree compared with it.</param>
  /// <returns>The differences; empty when the trees are equal.</returns>
  public static IReadOnlyList<TreeDifference> Compare(TreeDigest first, TreeDigest second) {
    ArgumentNullException.ThrowIfNull(first);
    ArgumentNullException.ThrowIfNull(second);
    var differences = new List<TreeDifference>();
    int left = 0, right = 0;
    while (left < first.Files.Count || right < second.Files.Count) {
      var order = left == first.Files.Count ? 1
        : right == second.Files.Count ? -1
        : string.CompareOrdinal(first.Files[left].Path, second.Files[right].Path);
      if (order < 0) {
        differences.Add(new(DifferenceKind.OnlyInFirst, first.Files[left++].Path));
      } else if (order > 0) {
        differences.Add(new(DifferenceKind.OnlyInSecond, second.Files[right++].Path));
      } else {
        var (a, b) = (first.Files[left++], second.Files[right++]);
        if (a.Sha256 != b.Sha256)
          differences.Add(new(DifferenceKind.Content, a.Path, FirstDifference(a.Content.Span, b.Content.Span)));
      }
    }

    return differences;
  }

  /// <summary>The first offset at which two byte sequences differ: the length of the shorter when it is a prefix of the longer.</summary>
  /// <param name="first">The first bytes.</param>
  /// <param name="second">The second bytes.</param>
  /// <returns>The offset; the common length when the two are equal.</returns>
  public static long FirstDifference(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second) => first.CommonPrefixLength(second);

}
