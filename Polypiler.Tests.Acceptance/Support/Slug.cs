using System.Globalization;
using System.Text.RegularExpressions;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary>
/// The stable name of a feature or scenario title: its lower-case form with every run of characters
/// outside <c>a-z</c> and <c>0-9</c> replaced by one <c>-</c>, without a leading or trailing <c>-</c>.
/// </summary>
public static partial class Slug {

  [GeneratedRegex("[^a-z0-9]+")]
  private static partial Regex Separators();

  /// <summary>The slug of a title.</summary>
  public static string Of(string title) => Separators().Replace(title.ToLowerInvariant(), "-").Trim('-');

  /// <summary>The slug of one row of a scenario outline: the slug of its title and the 1-based row number.</summary>
  public static string Of(string title, int row) => Of(title) + "-" + row.ToString(CultureInfo.InvariantCulture);

}
