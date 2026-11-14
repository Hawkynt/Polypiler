using System.Collections.Generic;

namespace Polypiler.Core.Frontends.CSharp;

public static partial class CSharpIntegerApi {

  /// <summary>Quotient and remainder at once: <c>Math.DivRem</c> with an out argument or as a pair, and the pair of every integer type's <c>DivRem</c>.</summary>
  private static IEnumerable<CSharpIntegerMember> Divisions => [
    new(Math, "DivRem", "Divide, Remainder", "only as a statement whose out argument or deconstructed pair are locals; it traps where the division does"),
    .. OfEveryType("DivRem", "Divide, Remainder", "only deconstructed into locals as a statement")
  ];

}
