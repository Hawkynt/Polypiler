using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Tooling.Licensing;

/// <summary>How far a licence reaches into the works derived from it, weakest first, so that a larger value is a stronger class.</summary>
public enum LicenceClass {

  /// <summary>A derived work may carry any licence, given the notice.</summary>
  Permissive = 0,

  /// <summary>The licensed part stays under its licence; a work that merely uses it does not (LGPL-3.0 §4).</summary>
  WeakCopyleft = 1,

  /// <summary>A work based on the licensed one is conveyed under the same licence (GPL-3.0 §5).</summary>
  StrongCopyleft = 2

}

/// <summary>
/// The class of one licence identifier. <i>Strong copyleft</i>: <c>GPL-2.0-or-later</c>,
/// <c>GPL-3.0-only</c>, <c>GPL-3.0-or-later</c> and any other identifier of the GPL or AGPL families;
/// <i>weak copyleft</i>: the LGPL identifiers of the policy and any other of the LGPL family;
/// <i>permissive</i>: every other identifier the policy allows. A reference-only or denied identifier
/// has no class: it cannot be part of an output at all.
/// </summary>
public static class LicenceClasses {

  /// <summary>The strong-copyleft identifiers the policy allows.</summary>
  public static IReadOnlyList<string> StrongCopyleft { get; } = ["GPL-2.0-or-later", "GPL-3.0-only", "GPL-3.0-or-later"];

  /// <summary>The weak-copyleft identifiers the policy allows.</summary>
  public static IReadOnlyList<string> WeakCopyleft { get; } = ["LGPL-2.0-only", "LGPL-2.0-or-later", "LGPL-2.1-or-later", "LGPL-3.0-only", "LGPL-3.0-or-later"];

  /// <summary>The label of every output with a strong-copyleft member: the GPL as the repository's own LGPL builds on it.</summary>
  public const string StrongLabel = "GPL-3.0-or-later";

  /// <summary>The label of every output whose strongest member is of weak copyleft.</summary>
  public const string WeakLabel = "LGPL-3.0-or-later";

  /// <summary>The class of an identifier by its family alone: the lists above, then the <c>GPL-</c>/<c>AGPL-</c> and <c>LGPL-</c> prefixes, then permissive.</summary>
  /// <param name="identifier">The SPDX identifier.</param>
  public static LicenceClass Family(string identifier) {
    if (StrongCopyleft.Contains(identifier) || identifier.StartsWith("GPL-", StringComparison.Ordinal) || identifier.StartsWith("AGPL-", StringComparison.Ordinal))
      return LicenceClass.StrongCopyleft;
    return WeakCopyleft.Contains(identifier) || identifier.StartsWith("LGPL-", StringComparison.Ordinal) ? LicenceClass.WeakCopyleft : LicenceClass.Permissive;
  }

  /// <summary>The class of an identifier under a policy, or <see langword="null"/> when the policy does not allow it in a distribution.</summary>
  /// <param name="identifier">The SPDX identifier.</param>
  /// <param name="policy">The policy.</param>
  public static LicenceClass? Of(string identifier, LicencePolicy policy)
    => policy.StatusOf(identifier, LicenceMode.Distribution) == LicenceStatus.Allowed ? Family(identifier) : null;

  /// <summary>The text of a class in <c>effective.json</c>: <c>permissive</c>, <c>weak-copyleft</c> or <c>strong-copyleft</c>.</summary>
  /// <param name="licenceClass">The class.</param>
  public static string Name(LicenceClass licenceClass) => licenceClass switch {
    LicenceClass.StrongCopyleft => "strong-copyleft",
    LicenceClass.WeakCopyleft => "weak-copyleft",
    _ => "permissive"
  };

}
