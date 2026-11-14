// xxHash32 (Collet, xxHash specification, "XXH32 algorithm description"): rotates, wrapping
// multiplications and little-endian loads. Written for this repository from the specification;
// the published values are xxHash32("", 0) = 0x02CC5D05, xxHash32("a", 0) = 0x550D7456,
// xxHash32("abc", 0) = 0x32D153FF and xxHash32("Nobody inspects the spammish repetition", 0) = 0xE2293B2F.
using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Polypiler.Fixtures.Prototype;

public static class XxHash32 {

  private const uint Prime1 = 2654435761u;
  private const uint Prime2 = 2246822519u;
  private const uint Prime3 = 3266489917u;
  private const uint Prime4 = 668265263u;
  private const uint Prime5 = 374761393u;

  public static uint Hash(byte[] data, uint seed) {
    int i = 0;
    uint h;
    if (data.Length >= 16) {
      uint v1 = seed + Prime1 + Prime2;
      uint v2 = seed + Prime2;
      uint v3 = seed;
      uint v4 = seed - Prime1;
      while (i + 16 <= data.Length) {
        v1 = BitOperations.RotateLeft(v1 + BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) * Prime2, 13) * Prime1;
        v2 = BitOperations.RotateLeft(v2 + BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 4)) * Prime2, 13) * Prime1;
        v3 = BitOperations.RotateLeft(v3 + BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 8)) * Prime2, 13) * Prime1;
        v4 = BitOperations.RotateLeft(v4 + BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 12)) * Prime2, 13) * Prime1;
        i += 16;
      }

      h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
    } else {
      h = seed + Prime5;
    }

    h += (uint)data.Length;
    while (i + 4 <= data.Length) {
      h += BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) * Prime3;
      h = BitOperations.RotateLeft(h, 17) * Prime4;
      i += 4;
    }

    while (i < data.Length) {
      h += data[i] * Prime5;
      h = BitOperations.RotateLeft(h, 11) * Prime1;
      ++i;
    }

    h ^= h >> 15;
    h *= Prime2;
    h ^= h >> 13;
    h *= Prime3;
    h ^= h >> 16;
    return h;
  }

}
