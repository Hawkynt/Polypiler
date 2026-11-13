// Adler-32 as RFC 1950 section 8.2 defines it: two sums modulo 65521, the largest prime below 2^16.
// Written for this repository; the test vector Adler-32("Wikipedia") = 0x11E60398 is the published one.
namespace Polypiler.Fixtures.Prototype;

public static class Adler32 {

  public static uint Compute(byte[] data) {
    uint a = 1;
    uint b = 0;
    for (int i = 0; i < data.Length; ++i) {
      a = (a + data[i]) % 65521;
      b = (b + a) % 65521;
    }

    return (b << 16) | a;
  }

}
