using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Polypiler.Core.Verification.Manifests;

/// <summary>
/// The licence <c>translate</c> states for its output (<c>licensing.outputLicence</c>): the licence of
/// the input together with the licences of the helpers it emitted (<see cref="EmittedHelper"/>),
/// reduced by the classes of <c>polypiler-tooling licenses scan</c> (owner decision of 2026-11-13).
/// An identifier of the GPL or AGPL family makes the output <see cref="StrongLabel"/>, else one of the
/// LGPL family <see cref="WeakLabel"/>, else the output is under the input's licence and every licence
/// of its helpers, joined by <c>AND</c>. An identifier is classed wherever it stands in the expression,
/// an alternative of an <c>OR</c> included, so the label is never weaker than the one the scan computes
/// with the policy; it may be stronger. Without a licence of the input nothing is asserted.
/// </summary>
public static class OutputLicence {

  /// <summary>What a manifest states when the licence of the input is not known.</summary>
  public const string NoAssertion = "NOASSERTION";

  /// <summary>The label of an output with a member of strong copyleft.</summary>
  public const string StrongLabel = "GPL-3.0-or-later";

  /// <summary>The label of an output whose strongest member is of weak copyleft.</summary>
  public const string WeakLabel = "LGPL-3.0-or-later";

  /// <summary>How many lines from the top a header may stand on.</summary>
  public const int HeaderLines = 30;

  private static readonly Regex _header = new(@"^\s*(?://+|#+|/\*+|\*)?\s*SPDX-License-Identifier:(.*)$", RegexOptions.CultureInvariant);
  private static readonly Regex _token = new(@"\(|\)|[^\s()]+", RegexOptions.CultureInvariant);
  private static readonly string[] _operators = ["AND", "OR", "WITH"];

  /// <summary>The licence a source states in an <c>SPDX-License-Identifier</c> line within its first <see cref="HeaderLines"/> lines, or <see langword="null"/>.</summary>
  /// <param name="source">The text of the source.</param>
  public static string? Header(string source) {
    ArgumentNullException.ThrowIfNull(source);
    foreach (var line in source.Split('\n').Take(HeaderLines)) {
      var match = _header.Match(line.TrimEnd('\r'));
      if (!match.Success)
        continue;

      var expression = match.Groups[1].Value.Trim();
      return expression.EndsWith("*/", StringComparison.Ordinal) ? expression[..^2].TrimEnd() : expression;
    }

    return null;
  }

  /// <summary>The licence of an output.</summary>
  /// <param name="input">The licence of the input as an SPDX expression, or <see langword="null"/> when it is not known.</param>
  /// <param name="helpers">The licences of the helpers the output carries; each an identifier or identifiers joined by <c>AND</c>.</param>
  /// <returns>
  /// <see cref="NoAssertion"/> without a licence of the input; the input's own expression without
  /// helpers; otherwise the label of the strongest class.
  /// </returns>
  public static string Of(string? input, IEnumerable<string> helpers) {
    ArgumentNullException.ThrowIfNull(helpers);
    if (string.IsNullOrWhiteSpace(input) || input.Trim() == NoAssertion)
      return NoAssertion;

    var expression = input.Trim();
    var added = helpers.SelectMany(Identifiers).Distinct(StringComparer.Ordinal).ToList();
    if (added.Count == 0)
      return expression;

    var own = Identifiers(expression).ToList();
    var all = own.Concat(added).ToList();
    if (all.Any(IsStrong))
      return StrongLabel;
    if (all.Any(IsWeak))
      return WeakLabel;

    // Identifiers joined by AND merge with the helpers'; an expression with a choice or an exception stays whole beside them.
    var helpersLicence = string.Join(" AND ", added.Order(StringComparer.Ordinal));
    var plain = _token.Matches(expression).All(token => token.Value is "AND" or "(" or ")" || own.Contains(token.Value));
    return plain ? string.Join(" AND ", all.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) : $"({expression}) AND {helpersLicence}";
  }

  /// <summary>The licence identifiers of an expression, the exceptions of a <c>WITH</c> left out.</summary>
  private static IEnumerable<string> Identifiers(string expression) {
    var tokens = _token.Matches(expression).Select(match => match.Value).ToList();
    for (var index = 0; index < tokens.Count; ++index)
      if (tokens[index] is not ("(" or ")") && !_operators.Contains(tokens[index]) && (index == 0 || tokens[index - 1] != "WITH"))
        yield return tokens[index];
  }

  private static bool IsStrong(string identifier) => identifier.StartsWith("GPL-", StringComparison.Ordinal) || identifier.StartsWith("AGPL-", StringComparison.Ordinal);

  private static bool IsWeak(string identifier) => identifier.StartsWith("LGPL-", StringComparison.Ordinal);

}
