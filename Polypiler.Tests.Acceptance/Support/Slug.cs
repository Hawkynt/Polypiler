using System.Globalization;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary>
/// The stable name of a feature or scenario title, by the rule of the evidence bundle
/// (<see cref="Polypiler.Core.Verification.Evidence.Slug.From"/>): its lower-case form with every run
/// of characters outside <c>a-z</c> and <c>0-9</c> replaced by one <c>-</c>, without a leading or
/// trailing <c>-</c>, at most 80 characters, and <c>scenario</c> when nothing is left.
/// </summary>
public static class Slug {

  /// <summary>The slug of a title.</summary>
  public static string Of(string title) => Polypiler.Core.Verification.Evidence.Slug.From(title);

  /// <summary>The slug of one row of a scenario outline: the slug of its title and the 1-based row number.</summary>
  public static string Of(string title, int row) => Of(title) + "-" + row.ToString(CultureInfo.InvariantCulture);

}
