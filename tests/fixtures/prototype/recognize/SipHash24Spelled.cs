// SipHash-2-4 (Aumasson and Bernstein, "SipHash: a fast short-input PRF", 2012) as bits/SipHash24.cs,
// with every rotate written as two shifts and every little-endian load as eight byte loads joined
// by shifts. The library-call version has 18 rotates and 3 loads. Written for this repository from
// the paper; the reference vectors of the paper's implementation give, for the key 00..0f and the
// message 00..0e, 0xa129ca6149be45e5, and for the empty message 0x726fdb47dd0e0e31.
namespace Polypiler.Fixtures.Prototype;

public static class SipHash24Spelled {

  public static ulong Hash(byte[] key, byte[] data) {
    ulong k0 = key[0] | (ulong)key[1] << 8 | (ulong)key[2] << 16 | (ulong)key[3] << 24
      | (ulong)key[4] << 32 | (ulong)key[5] << 40 | (ulong)key[6] << 48 | (ulong)key[7] << 56;
    ulong k1 = key[8] | (ulong)key[9] << 8 | (ulong)key[10] << 16 | (ulong)key[11] << 24
      | (ulong)key[12] << 32 | (ulong)key[13] << 40 | (ulong)key[14] << 48 | (ulong)key[15] << 56;
    ulong v0 = k0 ^ 0x736f6d6570736575UL;
    ulong v1 = k1 ^ 0x646f72616e646f6dUL;
    ulong v2 = k0 ^ 0x6c7967656e657261UL;
    ulong v3 = k1 ^ 0x7465646279746573UL;
    int whole = data.Length - data.Length % 8;
    for (int i = 0; i < whole; i += 8) {
      ulong m = data[i] | (ulong)data[i + 1] << 8 | (ulong)data[i + 2] << 16 | (ulong)data[i + 3] << 24
        | (ulong)data[i + 4] << 32 | (ulong)data[i + 5] << 40 | (ulong)data[i + 6] << 48 | (ulong)data[i + 7] << 56;
      v3 ^= m;
      for (int round = 0; round < 2; ++round) {
        v0 += v1; v1 = (v1 << 13) | (v1 >> 51); v1 ^= v0; v0 = (v0 << 32) | (v0 >> 32);
        v2 += v3; v3 = (v3 << 16) | (v3 >> 48); v3 ^= v2;
        v0 += v3; v3 = (v3 << 21) | (v3 >> 43); v3 ^= v0;
        v2 += v1; v1 = (v1 << 17) | (v1 >> 47); v1 ^= v2; v2 = (v2 << 32) | (v2 >> 32);
      }

      v0 ^= m;
    }

    ulong last = (ulong)data.Length << 56;
    for (int i = whole; i < data.Length; ++i)
      last |= (ulong)data[i] << (8 * (i - whole));

    v3 ^= last;
    for (int round = 0; round < 2; ++round) {
      v0 += v1; v1 = (v1 << 13) | (v1 >> 51); v1 ^= v0; v0 = (v0 << 32) | (v0 >> 32);
      v2 += v3; v3 = (v3 << 16) | (v3 >> 48); v3 ^= v2;
      v0 += v3; v3 = (v3 << 21) | (v3 >> 43); v3 ^= v0;
      v2 += v1; v1 = (v1 << 17) | (v1 >> 47); v1 ^= v2; v2 = (v2 << 32) | (v2 >> 32);
    }

    v0 ^= last;
    v2 ^= 0xff;
    for (int round = 0; round < 4; ++round) {
      v0 += v1; v1 = (v1 << 13) | (v1 >> 51); v1 ^= v0; v0 = (v0 << 32) | (v0 >> 32);
      v2 += v3; v3 = (v3 << 16) | (v3 >> 48); v3 ^= v2;
      v0 += v3; v3 = (v3 << 21) | (v3 >> 43); v3 ^= v0;
      v2 += v1; v1 = (v1 << 17) | (v1 >> 47); v1 ^= v2; v2 = (v2 << 32) | (v2 >> 32);
    }

    return v0 ^ v1 ^ v2 ^ v3;
  }

}
