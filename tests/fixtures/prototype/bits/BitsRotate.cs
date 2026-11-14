// The rotates of every width: BitOperations and the integer types' own. Written for this repository.
using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Polypiler.Fixtures.Prototype;

public static class BitsRotate {

  public static uint Left32(uint x, int n) => BitOperations.RotateLeft(x, n);

  public static ulong Right64(ulong x, int n) => BitOperations.RotateRight(x, n);

  public static ushort Left16(ushort x, int n) => ushort.RotateLeft(x, n);

  public static sbyte Right8(sbyte x, int n) => sbyte.RotateRight(x, n);

  public static long LeftSigned64(long x, int n) => long.RotateLeft(x, n);

}
