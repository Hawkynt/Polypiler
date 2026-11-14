using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core;

namespace Polypiler.Tooling.Licensing;

/// <summary>A member of the closure of an output: a source it is derived from, or a helper or runtime piece emitted into it.</summary>
/// <param name="Origin">Where the member comes from: a path, <c>owner/repo@sha:path</c>, or a helper's symbol.</param>
/// <param name="Expression">Its licence as an SPDX expression.</param>
public sealed record LicensedSource(string Origin, string Expression);

/// <summary>The licence an output is under.</summary>
/// <param name="Licence">The label, an SPDX expression.</param>
/// <param name="Class">Its class.</param>
/// <param name="Because">The origins of the members that decided it, sorted (ordinal) and distinct.</param>
public sealed record EffectiveLicenceResult(string Licence, LicenceClass Class, IReadOnlyList<string> Because);

/// <summary>
/// The licence of an output, computed from the licences of its closure (owner decision of 2026-11-13;
/// GPL-3.0 §5, LGPL-3.0 §4, GPL-3.0 §14):
/// <list type="number">
/// <item>any member of strong-copyleft class: <see cref="LicenceClasses.StrongLabel"/>;</item>
/// <item>otherwise any member of weak-copyleft class: <see cref="LicenceClasses.WeakLabel"/>;</item>
/// <item>otherwise the distinct permissive identifiers joined by <c> AND </c>, in ordinal order;</item>
/// <item>a member that is reference-only, denied or undetermined: no licence at all, the output may not be produced.</item>
/// </list>
/// The class of an expression: <c>A AND B</c> has the stronger class of its operands, <c>A OR B</c> the
/// class of its weakest allowed operand (the left one of equals), <c>WITH</c> the class of its licence
/// when the policy accepts the exception. A larger closure can only make the label stronger, and the
/// order of the members changes nothing.
/// </summary>
public static class EffectiveLicence {

  /// <summary>Computes the licence of a closure, reporting every member that cannot be part of an output.</summary>
  /// <param name="closure">The members.</param>
  /// <param name="policy">The policy that says which identifiers are allowed.</param>
  /// <param name="sink">Receives <c>PLY0024</c> for a reference-only or denied member and <c>PLY0029</c> for one whose expression does not parse.</param>
  /// <returns>The licence, or <see langword="null"/> when a member was reported.</returns>
  /// <exception cref="InvalidOperationException">The closure is empty.</exception>
  public static EffectiveLicenceResult? Compute(IEnumerable<LicensedSource> closure, LicencePolicy policy, IDiagnosticSink sink) {
    ArgumentNullException.ThrowIfNull(closure);
    ArgumentNullException.ThrowIfNull(policy);
    ArgumentNullException.ThrowIfNull(sink);
    var members = closure.ToList();
    if (members.Count == 0)
      throw new InvalidOperationException("The effective licence of an empty closure is undefined: an output is derived from something.");

    var resolved = new List<(string Origin, Resolution Resolution)>();
    var failed = false;
    foreach (var member in members) {
      SpdxExpression expression;
      try {
        expression = SpdxExpression.Parse(member.Expression);
      } catch (FormatException exception) {
        sink.Report(LicenceDiagnostics.Unknown(member.Origin, $"'{member.Expression}' is not an SPDX expression: {exception.Message}"));
        failed = true;
        continue;
      }

      if (Resolve(expression, policy) is { } resolution) {
        resolved.Add((member.Origin, resolution));
        continue;
      }

      var why = policy.Evaluate(expression, LicenceMode.Corpus) == LicenceStatus.ReferenceOnly ? "reference-only" : "denied";
      sink.Report(LicenceDiagnostics.Refused($"'{member.Origin}': licence '{member.Expression}' is {why} and cannot be part of an output"));
      failed = true;
    }

    if (failed)
      return null;

    var strongest = resolved.Max(member => member.Resolution.Class);
    var because = resolved.Where(member => member.Resolution.Class == strongest).Select(member => member.Origin).Distinct().Order(StringComparer.Ordinal).ToList();
    var licence = strongest switch {
      LicenceClass.StrongCopyleft => LicenceClasses.StrongLabel,
      LicenceClass.WeakCopyleft => LicenceClasses.WeakLabel,
      _ => string.Join(" AND ", resolved.SelectMany(member => member.Resolution.Identifiers).Distinct().Order(StringComparer.Ordinal))
    };
    return new(licence, strongest, because);
  }

  /// <summary>The class of an expression under a policy, or <see langword="null"/> when it does not parse or no resolution of it is allowed.</summary>
  /// <param name="expression">The SPDX expression.</param>
  /// <param name="policy">The policy.</param>
  public static LicenceClass? ClassOf(string expression, LicencePolicy policy) {
    try {
      return Resolve(SpdxExpression.Parse(expression), policy)?.Class;
    } catch (FormatException) {
      return null;
    }
  }

  /// <summary>
  /// Whether a stated licence is weaker than the effective one: its class is lower, or it has none
  /// (<c>NOASSERTION</c>, a denied identifier, text that does not parse). Stating a stronger licence is allowed.
  /// </summary>
  /// <param name="stated">The licence an output states.</param>
  /// <param name="effective">The licence <see cref="Compute"/> gave.</param>
  /// <param name="policy">The policy.</param>
  /// <exception cref="ArgumentException"><paramref name="effective"/> is no licence an output can be under.</exception>
  public static bool Understates(string stated, string effective, LicencePolicy policy) {
    var floor = ClassOf(effective, policy) ?? throw new ArgumentException($"'{effective}' is no licence an output can be under.", nameof(effective));
    return ClassOf(stated, policy) is not { } claimed || claimed < floor;
  }

  /// <summary>What an expression resolves to: its class, and the permissive identifiers it contributes.</summary>
  private sealed record Resolution(LicenceClass Class, IReadOnlyList<string> Identifiers);

  private static Resolution? Resolve(SpdxExpression expression, LicencePolicy policy) {
    switch (expression) {
      case SpdxLicence licence:
        return LicenceClasses.Of(licence.Id, policy) is { } licenceClass ? new(licenceClass, licenceClass == LicenceClass.Permissive ? [licence.Id] : []) : null;
      case SpdxWith with:
        return policy.AllowedExceptions.Contains(with.Exception) ? Resolve(with.Licence, policy) : null;
      case SpdxAnd and:
        if (Resolve(and.Left, policy) is not { } left || Resolve(and.Right, policy) is not { } right)
          return null;
        return new((LicenceClass)Math.Max((int)left.Class, (int)right.Class), [.. left.Identifiers, .. right.Identifiers]);
      default:
        var or = (SpdxOr)expression;
        var first = Resolve(or.Left, policy);
        var second = Resolve(or.Right, policy);
        if (first is null || second is null)
          return first ?? second;
        return second.Class < first.Class ? second : first;
    }
  }

}
