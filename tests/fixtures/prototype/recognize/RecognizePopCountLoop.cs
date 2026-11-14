// O0101: loops that count set bits - Kernighan's loop and the shift-test-count loop over a fixed
// width - and near misses. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizePopCountLoop {

  // expect: O0101
  public static int Kernighan(uint x) {
    int c = 0;
    while (x != 0) {
      c++;
      x &= x - 1;
    }

    return c;
  }

  // expect: O0101
  public static int KernighanSwapped(ulong x) {
    int c = 0;
    while (x != 0) {
      x &= x - 1;
      c++;
    }

    return c;
  }

  // expect: O0101
  public static int KernighanSigned(int x) {
    int c = 0;
    while (x != 0) {
      ++c;
      x = x & (x - 1);
    }

    return c;
  }

  // expect: O0101
  public static int ShiftTest(uint x) {
    int c = 0;
    for (int i = 0; i < 32; i++)
      c += (int)((x >> i) & 1);

    return c;
  }

  // expect: O0101
  public static int ShiftTestIf(ulong x) {
    int c = 0;
    for (int i = 0; i < 64; i++) {
      if (((x >> i) & 1) != 0)
        c++;
    }

    return c;
  }

  // expect: O0101
  public static int ShiftingValue(ushort value) {
    int c = 0;
    uint x = value;
    for (int i = 0; i < 32; i++) {
      c += (int)(x & 1);
      x >>= 1;
    }

    return c;
  }

  // expect: O0101
  public static long Accumulated(uint x, long c) {
    for (int i = 0; i < 32; i++)
      c += (x >> i) & 1;

    return c;
  }

  // expect: -
  public static int TooFewPasses(uint x) {
    int c = 0;
    for (int i = 0; i < 31; i++)
      c += (int)((x >> i) & 1);

    return c;
  }

  // expect: -
  public static int CountsTwice(uint x) {
    int c = 0;
    for (int i = 0; i < 32; i++)
      c += (int)((x >> i) & 3);

    return c;
  }

  // expect: -
  public static int WrongStep(uint x) {
    int c = 0;
    while (x != 0) {
      c++;
      x &= x - 2;
      if (c > 40)
        break;
    }

    return c;
  }

  // expect: -
  public static int CheckedCounter(uint x) {
    int c = 0;
    while (x != 0) {
      checked { c++; }
      x &= x - 1;
    }

    return c;
  }

}
