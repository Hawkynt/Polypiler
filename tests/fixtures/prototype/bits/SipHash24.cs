// SipHash-2-4 (Aumasson and Bernstein, "SipHash: a fast short-input PRF", 2012): the 64-bit rotates
// of BitOperations and the little-endian loads of BinaryPrimitives. Written for this repository from
// the paper; the reference vectors of the paper's implementation give, for the key 00..0f and the
// message 00..0e, 0xa129ca6149be45e5, and for the empty message 0x726fdb47dd0e0e31.
using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Polypiler.Fixtures.Prototype;

public static class SipHash24 {

  public static ulong Hash(byte[] key, byte[] data) {
    ulong k0 = BinaryPrimitives.ReadUInt64LittleEndian(key);
    ulong k1 = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(8));
    ulong v0 = k0 ^ 0x736f6d6570736575UL;
    ulong v1 = k1 ^ 0x646f72616e646f6dUL;
    ulong v2 = k0 ^ 0x6c7967656e657261UL;
    ulong v3 = k1 ^ 0x7465646279746573UL;
    int whole = data.Length - data.Length % 8;
    for (int i = 0; i < whole; i += 8) {
      ulong m = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(i, 8));
      v3 ^= m;
      for (int round = 0; round < 2; ++round) {
        v0 += v1; v1 = BitOperations.RotateLeft(v1, 13); v1 ^= v0; v0 = BitOperations.RotateLeft(v0, 32);
        v2 += v3; v3 = BitOperations.RotateLeft(v3, 16); v3 ^= v2;
        v0 += v3; v3 = BitOperations.RotateLeft(v3, 21); v3 ^= v0;
        v2 += v1; v1 = BitOperations.RotateLeft(v1, 17); v1 ^= v2; v2 = BitOperations.RotateLeft(v2, 32);
      }

      v0 ^= m;
    }

    ulong last = (ulong)data.Length << 56;
    for (int i = whole; i < data.Length; ++i)
      last |= (ulong)data[i] << (8 * (i - whole));

    v3 ^= last;
    for (int round = 0; round < 2; ++round) {
      v0 += v1; v1 = BitOperations.RotateLeft(v1, 13); v1 ^= v0; v0 = BitOperations.RotateLeft(v0, 32);
      v2 += v3; v3 = BitOperations.RotateLeft(v3, 16); v3 ^= v2;
      v0 += v3; v3 = BitOperations.RotateLeft(v3, 21); v3 ^= v0;
      v2 += v1; v1 = BitOperations.RotateLeft(v1, 17); v1 ^= v2; v2 = BitOperations.RotateLeft(v2, 32);
    }

    v0 ^= last;
    v2 ^= 0xff;
    for (int round = 0; round < 4; ++round) {
      v0 += v1; v1 = BitOperations.RotateLeft(v1, 13); v1 ^= v0; v0 = BitOperations.RotateLeft(v0, 32);
      v2 += v3; v3 = BitOperations.RotateLeft(v3, 16); v3 ^= v2;
      v0 += v3; v3 = BitOperations.RotateLeft(v3, 21); v3 ^= v0;
      v2 += v1; v1 = BitOperations.RotateLeft(v1, 17); v1 ^= v2; v2 = BitOperations.RotateLeft(v2, 32);
    }

    return v0 ^ v1 ^ v2 ^ v3;
  }

}
