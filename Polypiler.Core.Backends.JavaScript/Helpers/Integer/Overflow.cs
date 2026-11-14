// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  /// <summary>
  /// The overflow tests: an exact number or <c>BigInt</c> outside two bounds; of pairs the sign rule
  /// of a sum and a difference, and for a product the digits of the exact product of the magnitudes
  /// beyond the range.
  /// </summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> OverflowHelpers => [
    Same("$outside", Lines("function $outside(value, low, high) {", "  return value < low || value > high;", "}")),
    Same("$addovf64", Lines(
      "function $addovf64(a, b, signed) {",
      "  @const sum = $add64(a, b);",
      "  if (!signed) return $ucmp64(sum, a) < 0;",
      "  @const negative = a.hi >= 2147483648;",
      "  return negative === (b.hi >= 2147483648) && negative !== (sum.hi >= 2147483648);",
      "}")),
    Same("$subovf64", Lines(
      "function $subovf64(a, b, signed) {",
      "  if (!signed) return $ucmp64(a, b) < 0;",
      "  @const negative = a.hi >= 2147483648;",
      "  return negative !== (b.hi >= 2147483648) && negative !== ($sub64(a, b).hi >= 2147483648);",
      "}")),
    Same("$mulovf64", Lines(
      "function $mulovf64(a, b, signed) {",
      "  @const w = signed ? $mulw($mag64(a), $mag64(b)) : $mulw(a, b);",
      "  if ((w[4] | w[5] | w[6] | w[7]) !== 0) return true;",
      "  if (!signed) return false;",
      "  @const top = (w[3] << 16) | w[2];",
      "  @const negative = (a.hi >= 2147483648) !== (b.hi >= 2147483648);",
      "  return top < 0 && !(negative && top === -2147483648 && (w[1] | w[0]) === 0);",
      "}"))
  ];

}
