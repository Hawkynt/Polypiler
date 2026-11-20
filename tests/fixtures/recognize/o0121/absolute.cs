public static class Absolute {
  public static int XorSubtract(int x) {
    int m = x >> 31;
    return (x ^ m) - m;
  }

  public static int AddXor(int x) {
    int m = x >> 31;
    return (x + m) ^ m;
  }

  public static int CompareSelect(int x) => x < 0 ? 0 - x : x;

  public static sbyte Narrow(sbyte x) {
    sbyte m = (sbyte)(x >> 7);
    return (sbyte)((x ^ m) - m);
  }

  public static int Logical(int x) {
    int m = (int)((uint)x >> 31);
    return (x ^ m) - m;
  }

  public static int WrongShift(int x) {
    int m = x >> 30;
    return (x ^ m) - m;
  }

  public static int Trapping(int x) => x < 0 ? checked(-x) : x;

  public static int OtherMask(int x, int y) {
    int m = y >> 31;
    return (x ^ m) - m;
  }

  public static int MediaDroydDelta(int previous, int current) {
    int x = previous - current;
    int m = x >> 31;
    return (x ^ m) - m;
  }
}
