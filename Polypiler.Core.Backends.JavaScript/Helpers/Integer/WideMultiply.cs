using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  /// <summary>
  /// The upper half of a product of 32 bits in digits of 16 bits, each partial sum below 2^32 and so
  /// exact (Warren, <i>Hacker's Delight</i>, 2nd ed., §8-2); the signed one less the other factor for
  /// each negative factor (§8-3). Of two pairs it is the upper four digits of their full product,
  /// corrected the same way.
  /// </summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> WideMultiplyHelpers => [
    Same("$mulhi", """
      function $mulhi(a, b) {
        @const a0 = a & 0xFFFF;
        @const a1 = a >>> 16;
        @const b0 = b & 0xFFFF;
        @const b1 = b >>> 16;
        @const middle = a1 * b0 + ((a0 * b0) >>> 16);
        @const cross = a0 * b1 + (middle & 0xFFFF);
        return (a1 * b1 + Math.floor(middle / 65536) + Math.floor(cross / 65536)) >>> 0;
      }
      """),
    Same("$smulhi", """
      function $smulhi(a, b) {
        return ($mulhi(a, b) - (a < 0 ? b >>> 0 : 0) - (b < 0 ? a >>> 0 : 0)) | 0;
      }
      """),
    Same("$mulhi64", """
      function $mulhi64(a, b, signed) {
        @const w = $mulw(a, b);
        @let high = { hi: ((w[7] << 16) | w[6]) >>> 0, lo: ((w[5] << 16) | w[4]) >>> 0 };
        if (signed && a.hi >= 2147483648) high = $sub64(high, b);
        if (signed && b.hi >= 2147483648) high = $sub64(high, a);
        return high;
      }
      """)
  ];

}
