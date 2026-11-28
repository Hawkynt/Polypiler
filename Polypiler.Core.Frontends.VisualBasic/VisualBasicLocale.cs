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

  /// <summary>How the <c>Variant</c>s of the module are typed; by the framework's typing, written by the frontend, unless stated.</summary>
  public VisualBasicInference Inference { get; init; } = VisualBasicInference.Framework;

}

/// <summary>How the <c>Variant</c> locals, parameters and results of a module are typed (ADR 0300, #1591).</summary>
public enum VisualBasicInference {

  /// <summary>
  /// The shared framework's typing (#1585 to #1587), written by the frontend: the module is lowered
  /// with every <c>Variant</c> dynamic and analysed, then lowered again with the locals, clones and
  /// kinds the framework found, so that every typed operation is the frontend's own lowering of it.
  /// </summary>
  Framework,

  /// <summary>
  /// Every <c>Variant</c> stays dynamic and the module names Visual Basic's semantics table, which
  /// the de-dynamicization of the pipeline then types by rewriting the module.
  /// </summary>
  Dynamic

}
