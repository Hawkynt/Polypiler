public static class MulConst {
  public static uint Ten(uint x) => (x << 3) + (x << 1);

  public static uint TenNested(uint x) => ((x << 2) + x) << 1;

  public static uint Fifteen(uint x) => (x << 4) - x;

  public static uint MinusThree(uint x) => x - (x << 2);

  public static byte NarrowTen(byte x) => (byte)((x << 3) + (x << 1));

  public static uint OrTen(uint x) => (x << 3) | (x << 1);

  public static int CheckedTen(int x) => checked((x << 3) + (x << 1));

  public static uint TwoValues(uint x, uint y) => (x << 3) + (y << 1);

  public static uint VariableShift(uint x, int n) => (x << n) + x;

  public static uint One(uint x) => (x << 1) - x;
}
