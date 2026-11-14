// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  /// <summary>The rotates of 8 and 16 bits; those of 32 and 64 bits are the helpers <c>$rotl</c> and <c>$rotl64</c>.</summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> RotateHelpers => [
    Same("$rotln", """
      function $rotln(value, count, bits) {
        @const mask = (1 << bits) - 1;
        value &= mask;
        count &= bits - 1;
        return ((value << count) | (value >>> (bits - count))) & mask;
      }
      """),
    Same("$rotrn", """
      function $rotrn(value, count, bits) {
        @const mask = (1 << bits) - 1;
        value &= mask;
        count &= bits - 1;
        return ((value >>> count) | (value << (bits - count))) & mask;
      }
      """)
  ];

}
