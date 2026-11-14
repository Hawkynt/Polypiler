// SPDX-License-Identifier: MIT AND BSD-2-Clause
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The fused multiply-add of <c>float</c>, converted from musl's <c>fmaf.c</c>, whose origin is
/// FreeBSD's <c>lib/msun/src/s_fmaf.c</c> (BSD-2-Clause), which needs only doubles. It has a file of
/// its own so that the BSD-2-Clause covers only what is converted under it, wherever a backend emits
/// it. See <c>THIRD-PARTY-NOTICE.musl.txt</c>.
/// </summary>
public static partial class FloatOperations {

  /// <summary>
  /// <c>x * y + z</c> of <c>float</c> rounded once. The product of two floats is exact in a double,
  /// and the double sum differs from the exact one only where rounding it to a float again could
  /// round twice: when it lies halfway between two floats and is inexact, its last bit is moved
  /// towards the exact sum.
  /// </summary>
  /// <param name="x">The first factor.</param>
  /// <param name="y">The second factor.</param>
  /// <param name="z">The addend.</param>
  /// <returns>The correctly rounded result.</returns>
  public static float FusedMultiplyAddF(float x, float y, float z) {
    var xy = (double)x * y;
    var result = xy + z;
    var bits = BitConverter.DoubleToInt64Bits(result);
    var e = (int)(bits >> 52 & 0x7ff);
    // The bits of the double below those the float keeps: 29 of a normal float, more of a subnormal
    // one. From 54 on the double is below half the smallest float and rounds to zero as the exact
    // sum does; at 53 the half is the implicit bit, so the double is halfway when its fraction is 0.
    var dropped = e < 0x3ff - 126 ? 0x3ff - 126 - e + 29 : 29;
    if (dropped > 53)
      return (float)result;

    var half = dropped == 53 ? 0L : 1L << (dropped - 1);
    var rest = bits & (dropped == 53 ? (1L << 52) - 1 : (1L << dropped) - 1);
    if (rest != half || e == 0x7ff || (result - xy == z && result - z == xy))
      return (float)result;

    // An inexact result halfway between two floats: the last bit goes in the direction of the error.
    var below = bits < 0;
    var error = below == z > xy ? xy - result + z : z - result + xy;
    bits = below == error < 0 ? bits + 1 : bits - 1;
    return (float)BitConverter.Int64BitsToDouble(bits);
  }

}
