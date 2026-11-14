// O0110: parity as an exclusive-or fold by halves and as a population count's lowest bit, and
// near misses. Written for this repository from Warren, Hacker's Delight, 2nd ed., section 5-2.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizeParity {

  // expect: O0110
  public static int Fold32(uint x) {
    x ^= x >> 16;
    x ^= x >> 8;
    x ^= x >> 4;
    x ^= x >> 2;
    x ^= x >> 1;
    return (int)(x & 1);
  }

  // expect: O0110
  public static ulong Fold64(ulong x) {
    x ^= x >> 32;
    x ^= x >> 16;
    x ^= x >> 8;
    x ^= x >> 4;
    x ^= x >> 2;
    x ^= x >> 1;
    x &= 1;
    return x;
  }

  // expect: O0110
  public static int Short(ushort v) {
    int x = v;
    x ^= x >> 8;
    x ^= x >> 4;
    x ^= x >> 2;
    x ^= x >> 1;
    return x & 1;
  }

  // expect: O0110
  public static int Signed(int x) {
    x ^= x >> 16;
    x ^= x >> 8;
    x ^= x >> 4;
    x ^= x >> 2;
    x ^= x >> 1;
    return x & 1;
  }

  // expect: O0110
  public static int FromCount(uint x) => System.Numerics.BitOperations.PopCount(x) & 1;

  // expect: -
  public static int SkipsAStep(uint x) {
    x ^= x >> 16;
    x ^= x >> 8;
    x ^= x >> 4;
    x ^= x >> 1;
    return (int)(x & 1);
  }

  // expect: -
  public static uint NoMask(uint x) {
    x ^= x >> 16;
    x ^= x >> 8;
    x ^= x >> 4;
    x ^= x >> 2;
    x ^= x >> 1;
    return x;
  }

  // expect: -
  public static int StillRead(uint x, uint y) {
    x ^= x >> 16;
    x ^= x >> 8;
    x ^= x >> 4;
    x ^= x >> 2;
    x ^= x >> 1;
    y = x & 1;
    return (int)(x + y);
  }

}
