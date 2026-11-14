// Adler-32 as RFC 1950 section 8.2 defines it, over a slice of a larger buffer: two sums modulo
// 65521, the largest prime below 2^16. Written for this repository; the test vector
// Adler-32("Wikipedia") = 0x11E60398 is the published one.
using System;

namespace Polypiler.Fixtures.Prototype;

public static class Adler32Span {

  public static uint Compute(byte[] buffer, int offset, int length) => Of(buffer.AsSpan(offset, length));

  private static uint Of(ReadOnlySpan<byte> data) {
    uint a = 1;
    uint b = 0;
    foreach (byte value in data) {
      a = (a + value) % 65521;
      b = (b + a) % 65521;
    }

    return (b << 16) | a;
  }

}
