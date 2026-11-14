using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  /// <summary>
  /// Saturation: an exact result, number or <c>BigInt</c>, as the nearest value between two bounds;
  /// of pairs the wrapped result unless the overflow test says it left the range, then the bound on
  /// the side of the exact result.
  /// </summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> SaturatingHelpers => [
    Same("$sat", Lines("function $sat(value, low, high) {", "  return value < low ? low : value > high ? high : value;", "}")),
    Same("$addsat64", Lines(
      "function $addsat64(a, b, signed) {",
      $"  if (!$addovf64(a, b, signed)) return $add64(a, b);",
      $"  return !signed ? {AllOnes64} : a.hi >= 2147483648 ? {Smallest64} : {Largest64};",
      "}")),
    Same("$subsat64", Lines(
      "function $subsat64(a, b, signed) {",
      $"  if (!$subovf64(a, b, signed)) return $sub64(a, b);",
      $"  return !signed ? {Zero64} : a.hi >= 2147483648 ? {Smallest64} : {Largest64};",
      "}")),
    Same("$mulsat64", Lines(
      "function $mulsat64(a, b, signed) {",
      $"  if (!$mulovf64(a, b, signed)) return $mul64(a, b);",
      $"  return !signed ? {AllOnes64} : (a.hi >= 2147483648) !== (b.hi >= 2147483648) ? {Smallest64} : {Largest64};",
      "}"))
  ];

}
