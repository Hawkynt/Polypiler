public static class ZeroCount {
  public static int TrailingGuarded(uint x) {
    int c = 0;
    while (c < 32 && ((x >> c) & 1) == 0)
      c++;

    return c;
  }

  public static int TrailingUnguardedNonZero(uint x) {
    if (x == 0)
      return 32;

    int c = 0;
    while ((x & 1) == 0) {
      x >>= 1;
      c++;
    }

    return c;
  }

  public static int BitLength(uint v) {
    int n = 0;
    while (v != 0) {
      v >>= 1;
      n++;
    }

    return n;
  }

  public static int LeadingGuarded(uint x) {
    int c = 0;
    while (c < 32 && ((x << c) & 0x80000000u) == 0)
      c++;

    return c;
  }

  public static int TrailingUnguarded(uint x) {
    int c = 0;
    while ((x & 1) == 0) {
      x >>= 1;
      c++;
    }

    return c;
  }

  public static int BitLengthArithmetic(int v) {
    int n = 0;
    while (v != 0) {
      v >>= 1;
      n++;
    }

    return n;
  }

  public static int WrongBound(uint x) {
    int c = 0;
    while (c < 31 && ((x >> c) & 1) == 0)
      c++;

    return c;
  }

  public static int StartsAtOne(uint x) {
    int c = 1;
    while (c < 32 && ((x >> c) & 1) == 0)
      c++;

    return c;
  }

  public static int Logged(uint x, int[] log) {
    int c = 0;
    while (c < 32 && ((x >> c) & 1) == 0) {
      log[0] = c;
      c++;
    }

    return c;
  }
}
