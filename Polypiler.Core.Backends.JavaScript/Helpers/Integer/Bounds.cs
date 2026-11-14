// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  /// <summary>
  /// The clamp, which compares numbers and <c>BigInt</c>s alike and pairs by their signedness; the
  /// magnitude of 64 bits that wraps around; and the sign of an unsigned pair.
  /// </summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> BoundHelpers => [
    Same("$clamp", Lines(
      "function $clamp(value, low, high) {",
      "  if (low > high) " + Invalid,
      "  return value < low ? low : value > high ? high : value;",
      "}")),
    Same("$clamp64", Lines(
      "function $clamp64(value, low, high, signed) {",
      "  @const compare = signed ? $scmp64 : $ucmp64;",
      "  if (compare(low, high) > 0) " + Invalid,
      "  return compare(value, low) < 0 ? low : compare(value, high) > 0 ? high : value;",
      "}")),
    Wide("$absw64",
      Lines("function $absw64(value) {", "  return value < 0n ? -value : value;", "}"),
      Lines("function $absw64(value) {", "  return $mag64(value);", "}")),
    Same("$usign64", Lines("function $usign64(value) {", "  return (value.hi | value.lo) !== 0 ? 1 : 0;", "}"))
  ];

}
