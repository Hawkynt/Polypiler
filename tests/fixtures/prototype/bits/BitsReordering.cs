// The byte swap of every width. Written for this repository.
using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Polypiler.Fixtures.Prototype;

public static class BitsReordering {

  public static uint Swap32(uint x) => BinaryPrimitives.ReverseEndianness(x);

  public static short Swap16(short x) => BinaryPrimitives.ReverseEndianness(x);

  public static ulong Swap64(ulong x) => BinaryPrimitives.ReverseEndianness(x);

  public static byte Swap8(byte x) => BinaryPrimitives.ReverseEndianness(x);

}
