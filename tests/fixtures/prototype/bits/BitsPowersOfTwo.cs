// The tests for and the rounding up to a power of two. Written for this repository.
using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Polypiler.Fixtures.Prototype;

public static class BitsPowersOfTwo {

  public static bool Power(int x) => BitOperations.IsPow2(x);

  public static bool PowerShort(short x) => short.IsPow2(x);

  public static uint Up(uint x) => BitOperations.RoundUpToPowerOf2(x);

  public static ulong UpWide(ulong x) => BitOperations.RoundUpToPowerOf2(x);

}
