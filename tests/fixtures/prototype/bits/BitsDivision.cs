// Quotient and remainder at once: Math.DivRem with an out argument and the pair of DivRem. Written for this repository.
using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Polypiler.Fixtures.Prototype;

public static class BitsDivision {

  public static int Both(int a, int b) { int q = Math.DivRem(a, b, out int r); return q * 1000 + r; }

  public static long Pair(long a, long b) { var (q, r) = long.DivRem(a, b); return q ^ r; }

  public static uint Unsigned(uint a, uint b) { uint q; uint r; (q, r) = uint.DivRem(a, b); return q + r; }

  public static int Narrow(sbyte a, sbyte b) { var (q, r) = sbyte.DivRem(a, b); return q * 1000 + r; }

}
