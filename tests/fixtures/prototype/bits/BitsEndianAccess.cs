// Endian loads and stores through BinaryPrimitives and BitConverter. Written for this repository.
using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Polypiler.Fixtures.Prototype;

public static class BitsEndianAccess {

  public static uint Little(byte[] data, int index) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index));

  public static short Big(byte[] data, int index) => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(index, 2));

  public static long Converted(byte[] data, int index) => BitConverter.ToInt64(data, index);

  public static byte[] Store(byte[] data, int index, ulong value) { BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(index), value); return data; }

  public static byte[] StoreShort(byte[] data, ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(data, value); return data; }

}
