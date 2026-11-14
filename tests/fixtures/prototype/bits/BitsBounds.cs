// Bounds of integers: clamp, minimum, maximum, magnitude and sign of the integer types and of Math. Written for this repository.
using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Polypiler.Fixtures.Prototype;

public static class BitsBounds {

  public static int Clamped(int x) => Math.Clamp(x, -100, 100);

  public static byte ClampedByte(byte x, byte low) => byte.Clamp(x, (byte)(low / 2), (byte)200);

  public static long Smaller(long a, long b) => long.Min(a, b);

  public static ushort Larger(ushort a, ushort b) => ushort.Max(a, b);

  public static short Magnitude(short x) => short.Abs(x);

  public static int Signum(ulong x) => ulong.Sign(x);

}
