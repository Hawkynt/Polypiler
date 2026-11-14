// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  /// <summary>
  /// The byte swap, written out for each width so that it costs no more than the shifts it stands
  /// for, and the bit reversal, built up bit by bit in a number, which is exact below 2^53; of 64 bits
  /// on the unsigned <c>BigInt</c>, or as the swapped halves.
  /// </summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> ReorderingHelpers => [
    Same("$bswap", """
      function $bswap(value, bits) {
        if (bits === 32) return (((value & 0xFF) << 24) | ((value & 0xFF00) << 8) | ((value >>> 8) & 0xFF00) | ((value >>> 24) & 0xFF)) >>> 0;
        if (bits === 16) return ((value & 0xFF) << 8) | ((value >>> 8) & 0xFF);
        return value & 0xFF;
      }
      """),
    Wide("$bswap64", """
      function $bswap64(value) {
        @let rest = BigInt.asUintN(64, value);
        @let result = 0n;
        for (@let count = 0; count < 8; ++count) {
          result = (result << 8n) | (rest & 0xFFn);
          rest >>= 8n;
        }
        return result;
      }
      """, """
      function $bswap64(value) {
        return { hi: $bswap(value.lo, 32), lo: $bswap(value.hi, 32) };
      }
      """),
    Same("$brev", """
      function $brev(value, bits) {
        @let result = 0;
        for (@let index = 0; index < bits; ++index) result = result * 2 + ((value >>> index) & 1);
        return result;
      }
      """),
    Wide("$brev64", """
      function $brev64(value) {
        @const bits = BigInt.asUintN(64, value);
        return (BigInt($brev(Number(bits & 0xFFFFFFFFn), 32)) << 32n) | BigInt($brev(Number(bits >> 32n), 32));
      }
      """, """
      function $brev64(value) {
        return { hi: $brev(value.lo, 32), lo: $brev(value.hi, 32) };
      }
      """)
  ];

}
