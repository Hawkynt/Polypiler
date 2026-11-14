using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Polypiler.Core;

/// <summary>
/// Code a backend copied into a generated file beside the translated program: a helper function or a
/// portable floating-point implementation. Polypiler's own such code is released under
/// <see cref="OwnLicence"/> by <see cref="GrantFile"/> (owner decision of 2026-11-14), so it never puts
/// the compiler's LGPL on a translated program; code converted from a third-party source keeps the
/// licence its file states.
/// </summary>
/// <param name="Symbol">The name of the helper in the output manifest; unique within a file.</param>
/// <param name="Licence">Its licence, as an SPDX expression.</param>
/// <param name="Origin">The source it was copied from: <c>Hawkynt/Polypiler:</c> and the path of the file or folder in the repository.</param>
/// <param name="CallSites">The uses of it in the file outside its own text, those of other helpers included.</param>
/// <param name="Size">The length of its text in the file, in UTF-8 bytes.</param>
/// <param name="DependsOn">The symbols of the other helpers its text uses, in ordinal order.</param>
public sealed record EmittedHelper(string Symbol, string Licence, string Origin, int CallSites, int Size, IReadOnlyList<string> DependsOn) {

  /// <summary>Whether two helpers are the same: every field equal, the dependencies element by element.</summary>
  /// <param name="other">The other helper.</param>
  public bool Equals(EmittedHelper? other)
    => other is not null && this.Symbol == other.Symbol && this.Licence == other.Licence && this.Origin == other.Origin
      && this.CallSites == other.CallSites && this.Size == other.Size && this.DependsOn.SequenceEqual(other.DependsOn);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Symbol, this.Licence, this.Origin, this.CallSites, this.Size, this.DependsOn.Count);

  /// <summary>The licence of the code of Polypiler's own that a translation copies into its output.</summary>
  public const string OwnLicence = "MIT";

  /// <summary>The file of the repository that grants <see cref="OwnLicence"/> for emitted code.</summary>
  public const string GrantFile = "LICENSE-OUTPUT.md";

  /// <summary>Where the grant can be read.</summary>
  public const string GrantUrl = "https://github.com/Hawkynt/Polypiler/blob/main/" + GrantFile;

  /// <summary>The repository the sources of the helpers lie in, as an <see cref="Origin"/> names it.</summary>
  public const string Repository = "Hawkynt/Polypiler";

  /// <summary>The origin of a helper whose source lies at a path of the repository.</summary>
  /// <param name="path">The path, with <c>/</c> between its folders.</param>
  public static string OriginOf(string path) => $"{Repository}:{path}";

  /// <summary>
  /// Measures the helpers of a file. A use of a helper is its name standing as a word of its own:
  /// neither preceded by a letter, a digit, <c>_</c>, <c>$</c> or <c>.</c>, nor followed by a letter,
  /// a digit, <c>_</c> or <c>$</c> — a call, or the read of a helper that is a value. The texts of
  /// the helpers of the same name, its declaration and those of its overloads, are not searched for
  /// its call sites.
  /// </summary>
  /// <param name="helpers">The helpers as the backend wrote them.</param>
  /// <param name="file">The whole text of the file.</param>
  /// <returns>One helper per text, in the order given.</returns>
  /// <exception cref="ArgumentException">A symbol is given twice, or a text is not part of the file.</exception>
  public static IReadOnlyList<EmittedHelper> Measure(IReadOnlyList<HelperText> helpers, string file) {
    ArgumentNullException.ThrowIfNull(helpers);
    ArgumentNullException.ThrowIfNull(file);
    if (helpers.Select(helper => helper.Symbol).Distinct(StringComparer.Ordinal).Count() != helpers.Count)
      throw new ArgumentException("Every helper of a file needs a symbol of its own.", nameof(helpers));
    if (helpers.FirstOrDefault(helper => !file.Contains(helper.Text, StringComparison.Ordinal)) is { } missing)
      throw new ArgumentException($"The text of the helper '{missing.Symbol}' is not part of the file.", nameof(helpers));

    return [.. helpers.Select(helper => {
      var call = Call(helper.Name);
      var rest = helpers.Where(other => other.Name == helper.Name).Aggregate(file, (text, other) => Without(text, other.Text));
      var calls = helpers
        .Where(other => other.Name != helper.Name && Call(other.Name).IsMatch(helper.Text))
        .Select(other => other.Symbol)
        .Order(StringComparer.Ordinal);
      return new EmittedHelper(helper.Symbol, helper.Licence, helper.Origin, call.Count(rest), Encoding.UTF8.GetByteCount(helper.Text), [.. calls]);
    })];
  }

  /// <summary>
  /// The line a generated file's header carries when it has helpers: the licences of the helpers,
  /// each identifier once in ordinal order, and where the grant can be read; <see langword="null"/>
  /// without helpers.
  /// </summary>
  /// <param name="licences">The licence of each helper of the file, identifiers joined by <c>AND</c>.</param>
  public static string? HeaderLine(IEnumerable<string> licences) {
    ArgumentNullException.ThrowIfNull(licences);
    var identifiers = licences.SelectMany(licence => licence.Split(" AND ", StringSplitOptions.TrimEntries)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    return identifiers.Count == 0 ? null : $"Helpers added by {ProductInfo.Name}: {string.Join(" AND ", identifiers)} ({GrantUrl}); the translated code keeps the licence of its source.";
  }

  private static Regex Call(string name) => new($@"(?<![\w$.]){Regex.Escape(name)}(?![\w$])", RegexOptions.CultureInvariant);

  private static string Without(string text, string part) {
    var index = text.IndexOf(part, StringComparison.Ordinal);
    return index < 0 ? text : string.Concat(text.AsSpan(0, index), text.AsSpan(index + part.Length));
  }

}

/// <summary>The text of a helper as a backend wrote it into a file, before <see cref="EmittedHelper.Measure"/> measures it.</summary>
/// <param name="Symbol">The name of the helper in the output manifest.</param>
/// <param name="Name">The name it is called by in the file.</param>
/// <param name="Licence">Its licence, as an SPDX expression.</param>
/// <param name="Origin">The source it was copied from.</param>
/// <param name="Text">Its text, as it stands in the file.</param>
public sealed record HelperText(string Symbol, string Name, string Licence, string Origin, string Text);
