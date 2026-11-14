// Full products: Math.BigMul of 32 bits, and of 64 bits with the lower half in an out argument. Written for this repository.
using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Polypiler.Fixtures.Prototype;

public static class BitsWideMultiply {

  public static long Signed(int a, int b) => Math.BigMul(a, b);

  public static ulong Unsigned(uint a, uint b) => Math.BigMul(a, b);

  public static ulong High(ulong a, ulong b) { ulong high = Math.BigMul(a, b, out ulong low); return high ^ (low >> 7); }

  public static long HighSigned(long a, long b) { long low; long high = Math.BigMul(a, b, out low); return high - low; }

}
