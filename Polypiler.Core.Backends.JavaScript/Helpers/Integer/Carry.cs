// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  /// <summary>
  /// Carry and borrow of two pairs, half by half: the sum or difference of two lower halves and the
  /// incoming bit is exact in a number, and what it leaves above or below the 32 bits goes on to the
  /// upper halves.
  /// </summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> CarryHelpers => [
    Same("$adc64", Lines(
      "function $adc64(a, b, carry) {",
      "  @const lo = a.lo + b.lo + (carry ? 1 : 0);",
      "  return { hi: (a.hi + b.hi + (lo > 4294967295 ? 1 : 0)) >>> 0, lo: lo >>> 0 };",
      "}")),
    Same("$adcout64", Lines(
      "function $adcout64(a, b, carry) {",
      "  @const lo = a.lo + b.lo + (carry ? 1 : 0);",
      "  return a.hi + b.hi + (lo > 4294967295 ? 1 : 0) > 4294967295;",
      "}")),
    Same("$sbb64", Lines(
      "function $sbb64(a, b, borrow) {",
      "  @const lo = a.lo - b.lo - (borrow ? 1 : 0);",
      "  return { hi: (a.hi - b.hi - (lo < 0 ? 1 : 0)) >>> 0, lo: lo >>> 0 };",
      "}")),
    Same("$sbbout64", Lines(
      "function $sbbout64(a, b, borrow) {",
      "  @const lo = a.lo - b.lo - (borrow ? 1 : 0);",
      "  return a.hi - b.hi - (lo < 0 ? 1 : 0) < 0;",
      "}"))
  ];

}
