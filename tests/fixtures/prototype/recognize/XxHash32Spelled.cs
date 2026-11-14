// xxHash32 (Collet, xxHash specification, "XXH32 algorithm description") as bits/XxHash32.cs, with
// every rotate written as two shifts and every little-endian load as four byte loads joined by
// shifts - the way code without BitOperations and BinaryPrimitives spells them. The library-call
// version has 10 rotates and 5 loads. Written for this repository from the specification; the
// published values are xxHash32("", 0) = 0x02CC5D05, xxHash32("a", 0) = 0x550D7456,
// xxHash32("abc", 0) = 0x32D153FF and xxHash32("Nobody inspects the spammish repetition", 0) = 0xE2293B2F.
namespace Polypiler.Fixtures.Prototype;

public static class XxHash32Spelled {

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
        v1 += (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24) * Prime2;
        v1 = ((v1 << 13) | (v1 >> 19)) * Prime1;
        v2 += (uint)(data[i + 4] | data[i + 5] << 8 | data[i + 6] << 16 | data[i + 7] << 24) * Prime2;
        v2 = ((v2 << 13) | (v2 >> 19)) * Prime1;
        v3 += (uint)(data[i + 8] | data[i + 9] << 8 | data[i + 10] << 16 | data[i + 11] << 24) * Prime2;
        v3 = ((v3 << 13) | (v3 >> 19)) * Prime1;
        v4 += (uint)(data[i + 12] | data[i + 13] << 8 | data[i + 14] << 16 | data[i + 15] << 24) * Prime2;
        v4 = ((v4 << 13) | (v4 >> 19)) * Prime1;
        i += 16;
      }

      h = ((v1 << 1) | (v1 >> 31)) + ((v2 << 7) | (v2 >> 25)) + ((v3 << 12) | (v3 >> 20)) + ((v4 << 18) | (v4 >> 14));
    } else {
      h = seed + Prime5;
    }

    h += (uint)data.Length;
    while (i + 4 <= data.Length) {
      h += (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24) * Prime3;
      h = ((h << 17) | (h >> 15)) * Prime4;
      i += 4;
    }

    while (i < data.Length) {
      h += data[i] * Prime5;
      h = ((h << 11) | (h >> 21)) * Prime1;
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
