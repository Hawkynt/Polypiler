// The counts of bits of every width, as BitOperations and the integer types have them. Written for this repository.
using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Polypiler.Fixtures.Prototype;

public static class BitsCounts {

  public static int Population(ulong x) => BitOperations.PopCount(x);

  public static int Leading(uint x) => BitOperations.LeadingZeroCount(x);

  public static int TrailingSigned(int x) => BitOperations.TrailingZeroCount(x);

  public static byte LeadingByte(byte x) => byte.LeadingZeroCount(x);

  public static short TrailingShort(short x) => short.TrailingZeroCount(x);

  public static int Logarithm(ulong x) => BitOperations.Log2(x);

  public static ushort PopulationShort(ushort x) => ushort.PopCount(x);

}
