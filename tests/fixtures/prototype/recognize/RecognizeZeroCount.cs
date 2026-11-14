// O0103: loops that count zero bits from the bottom or from the top until a set bit, and near
// misses. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizeZeroCount {

  // expect: O0103
  public static int Trailing(uint x) {
    int n = 0;
    while (n < 32 && (x & 1) == 0) {
      x >>= 1;
      n++;
    }

    return n;
  }

  // expect: O0103
  public static int TrailingTestFirst(ulong x) {
    int n = 0;
    while ((x & 1) == 0 && n < 64) {
      n++;
      x >>= 1;
    }

    return n;
  }

  // expect: O0103
  public static int TrailingByPosition(uint x) {
    int n;
    for (n = 0; n < 32 && ((x >> n) & 1) == 0; n++) {
    }

    return n;
  }

  // expect: O0103
  public static int TrailingSigned(int x) {
    int n = 0;
    while (n < 32 && (x & 1) == 0) {
      x >>= 1;
      n++;
    }

    return n + x;
  }

  // expect: O0103
  public static int Leading(uint x) {
    int n = 0;
    while (n < 32 && (x & 0x80000000u) == 0) {
      x <<= 1;
      n++;
    }

    return n;
  }

  // expect: O0103
  public static int LeadingByMask(ulong x) {
    int n = 0;
    while (n < 64 && (x & (0x8000000000000000UL >> n)) == 0)
      n++;

    return n;
  }

  // expect: O0103
  public static uint LeadingShifted(ushort value) {
    uint x = value;
    int n = 0;
    while (n < 32 && (x & 0x80000000u) == 0) {
      x <<= 1;
      n++;
    }

    return x + (uint)n;
  }

  // expect: -
  public static int Unbounded(uint x) {
    int n = 0;
    if (x == 0)
      return 32;

    while ((x & 1) == 0) {
      x >>= 1;
      n++;
    }

    return n;
  }

  // expect: -
  public static int WrongBound(uint x) {
    int n = 0;
    while (n < 31 && (x & 1) == 0) {
      x >>= 1;
      n++;
    }

    return n;
  }

  // expect: -
  public static int WrongBit(uint x) {
    int n = 0;
    while (n < 32 && (x & 2) == 0) {
      x >>= 1;
      n++;
    }

    return n;
  }

  // expect: -
  public static int StartsAtOne(uint x) {
    int n = 1;
    while (n < 32 && (x & 1) == 0) {
      x >>= 1;
      n++;
    }

    return n;
  }

}
