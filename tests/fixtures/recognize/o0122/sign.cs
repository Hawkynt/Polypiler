public static class Sign {
  public static int CompareDifference(int x) => (x > 0 ? 1 : 0) - (x < 0 ? 1 : 0);

  public static int ShiftOr(int x) => (x >> 31) | (int)((uint)-x >> 31);

  public static int Select(int x) => x > 0 ? 1 : x < 0 ? -1 : 0;

  public static int Narrow(sbyte x) => (x > 0 ? 1 : 0) - (x < 0 ? 1 : 0);

  public static int OrOne(int x) => (x >> 31) | 1;

  public static int ShiftOrArithmetic(int x) => (x >> 31) | (-x >> 31);

  public static int WrongShift(int x) => (x >> 30) | (int)((uint)-x >> 31);

  public static int Swapped(int x) => (x < 0 ? 1 : 0) - (x > 0 ? 1 : 0);

  public static int Unsigned(uint x) => (x > 0 ? 1 : 0) - (x < 0 ? 1 : 0);

  public static int TwoValues(int x, int y) => (x > 0 ? 1 : 0) - (y < 0 ? 1 : 0);
}
