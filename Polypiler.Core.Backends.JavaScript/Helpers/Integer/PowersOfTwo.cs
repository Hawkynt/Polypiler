// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  /// <summary>
  /// The test for a power of two, on the value - a negative one is none - and the rounding up to one
  /// on the pattern, by doubling: up to 33 doublings of a number, which stays exact, and up to 65 of a
  /// <c>BigInt</c>, cut back to its 64 bits.
  /// </summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> PowerHelpers => [
    Same("$ispow2", """
      function $ispow2(value) {
        return value > 0 && (value & (value - 1)) === 0;
      }
      """),
    Wide("$ispow264", """
      function $ispow264(value, signed) {
        return value > 0n && (value & (value - 1n)) === 0n;
      }
      """, """
      function $ispow264(value, signed) {
        if (signed && value.hi >= 2147483648) return false;
        return value.hi === 0 ? $ispow2(value.lo) : value.lo === 0 && $ispow2(value.hi);
      }
      """),
    Same("$rup2", """
      function $rup2(value, bits) {
        @const pattern = bits === 32 ? value >>> 0 : value & ((1 << bits) - 1);
        if (pattern === 0) return 0;
        @let power = 1;
        while (power < pattern) power *= 2;
        return power;
      }
      """),
    Wide("$rup264", """
      function $rup264(value) {
        @const pattern = BigInt.asUintN(64, value);
        if (pattern === 0n) return 0n;
        @let power = 1n;
        while (power < pattern) power <<= 1n;
        return BigInt.asUintN(64, power);
      }
      """, """
      function $rup264(value) {
        if (value.hi === 0) return value.lo <= 2147483648 ? { hi: 0, lo: $rup2(value.lo, 32) } : { hi: 1, lo: 0 };
        if (value.hi > 2147483648 || (value.hi === 2147483648 && value.lo !== 0)) return { hi: 0, lo: 0 };
        return { hi: $rup2(value.lo === 0 ? value.hi : value.hi + 1, 32), lo: 0 };
      }
      """)
  ];

}
