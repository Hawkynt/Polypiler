using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.VisualBasic;

/// <summary>
/// The regional settings a classic Visual Basic module runs under, as far as they decide what it
/// computes: the decimal separator <c>CStr</c> writes and <c>CDbl</c> reads, and the digit group
/// separator <c>CDbl</c> skips ([MS-VBAL] §5.5.1.2.4). <c>Val</c> and <c>Str</c> read and write a
/// point in every locale. The default, <c>en-US</c>, has the separators of the invariant culture.
/// </summary>
public sealed record VisualBasicLocale {

  private VisualBasicLocale(string name, char @decimal, char group) {
    this.Name = name;
    this.Decimal = @decimal;
    this.Group = group;
  }

  /// <summary>The name, as Windows names the locale: <c>en-US</c>, <c>de-DE</c>, ….</summary>
  public string Name { get; }

  /// <summary>The decimal separator.</summary>
  public char Decimal { get; }

  /// <summary>The digit group separator.</summary>
  public char Group { get; }

  /// <summary>English (United States), the separators of the invariant culture: <c>1,234.5</c>.</summary>
  public static VisualBasicLocale EnUs { get; } = new("en-US", '.', ',');

  /// <summary>English (United Kingdom): <c>1,234.5</c>.</summary>
  public static VisualBasicLocale EnGb { get; } = new("en-GB", '.', ',');

  /// <summary>German (Germany): <c>1.234,5</c>.</summary>
  public static VisualBasicLocale DeDe { get; } = new("de-DE", ',', '.');

  /// <summary>French (France): <c>1 234,5</c> with a no-break space.</summary>
  public static VisualBasicLocale FrFr { get; } = new("fr-FR", ',', '\u00A0');

  /// <summary>The locales the frontend knows, the default first.</summary>
  public static IReadOnlyList<VisualBasicLocale> All { get; } = [EnUs, EnGb, DeDe, FrFr];

  /// <summary>The locale a translation assumes when none is named.</summary>
  public static VisualBasicLocale Default => EnUs;

  /// <summary>Reads a locale name, in any case.</summary>
  /// <param name="name">The name.</param>
  /// <param name="locale">The locale, or <see langword="null"/>.</param>
  /// <returns>Whether the name is one of <see cref="All"/>.</returns>
  public static bool TryParse(string? name, out VisualBasicLocale? locale) {
    locale = All.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
    return locale is not null;
  }

  /// <summary>The name of the locale.</summary>
  public override string ToString() => this.Name;

}

/// <summary>How a classic Visual Basic module is imported.</summary>
public sealed record VisualBasicImportOptions {

  /// <summary>The regional settings the module runs under; <see cref="VisualBasicLocale.Default"/> unless stated.</summary>
  public VisualBasicLocale Locale { get; init; } = VisualBasicLocale.Default;

  /// <summary>Which inference types the <c>Variant</c>s of the module; the frontend's own unless stated.</summary>
  public VisualBasicInference Inference { get; init; } = VisualBasicInference.Frontend;

}

/// <summary>Which inference types the <c>Variant</c> locals, parameters and results of a module (ADR 0300).</summary>
public enum VisualBasicInference {

  /// <summary>The frontend's own: a fixed point within each procedure (#1556) and clones across the module (#1564).</summary>
  Frontend,

  /// <summary>
  /// The shared framework: every <c>Variant</c> is lowered dynamic, the module names Visual Basic's
  /// semantics table, and the de-dynamicization types it (#1585 to #1587). The migration of #1591
  /// makes this the default once its output equals the frontend's.
  /// </summary>
  Framework

}
