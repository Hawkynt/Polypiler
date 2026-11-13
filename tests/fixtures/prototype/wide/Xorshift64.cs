// The xorshift generators with 64 bits of state: Marsaglia's with the shifts 13, 7, 17, and Vigna's
// xorshift64* with the shifts 12, 25, 27 and the multiplier 0x2545F4914F6CDD1D. Written for this
// repository. The signed variant is what a port that used long would write.
namespace Polypiler.Fixtures.Prototype;

public static class Xorshift64 {

  public static ulong Next(ulong x) {
    x ^= x << 13;
    x ^= x >> 7;
    x ^= x << 17;
    return x;
  }

  public static ulong NextStar(ulong x) {
    x ^= x >> 12;
    x ^= x << 25;
    x ^= x >> 27;
    return x * 0x2545F4914F6CDD1D;
  }

  public static long NextSigned(long x) {
    x ^= x << 13;
    x ^= x >>> 7;
    x ^= x << 17;
    return x;
  }

  public static ulong Skip(ulong x, int count) {
    for (int i = 0; i < count; ++i)
      x = Next(x);

    return x;
  }

}
