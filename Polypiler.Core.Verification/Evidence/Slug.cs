using System;
using System.Text.RegularExpressions;

namespace Polypiler.Core.Verification.Evidence;

/// <summary>
/// The folder name of a feature or scenario title: the title in lower case (invariant culture), every
/// run of characters outside <c>a-z</c> and <c>0-9</c> replaced by one <c>-</c>, <c>-</c> trimmed at
/// both ends, cut to <see cref="MaxLength"/> characters and trimmed again. A title with nothing left
/// is <see cref="Empty"/>.
/// </summary>
public static partial class Slug {

  /// <summary>The longest slug.</summary>
  public const int MaxLength = 80;

  /// <summary>The slug of a title that has no letter or digit of <c>a-z</c>, <c>0-9</c>.</summary>
  public const string Empty = "scenario";

  [GeneratedRegex("[^a-z0-9]+")]
  private static partial Regex Separators();

  /// <summary>The slug of a title.</summary>
  /// <param name="title">The title.</param>
  /// <returns>The slug: at most <see cref="MaxLength"/> characters of <c>a-z</c>, <c>0-9</c> and <c>-</c>, neither starting nor ending in <c>-</c>.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="title"/> is <see langword="null"/>.</exception>
  public static string From(string title) {
    ArgumentNullException.ThrowIfNull(title);
    var slug = Separators().Replace(title.ToLowerInvariant(), "-").Trim('-');
    if (slug.Length > MaxLength)
      slug = slug[..MaxLength].TrimEnd('-');

    return slug.Length == 0 ? Empty : slug;
  }

}
