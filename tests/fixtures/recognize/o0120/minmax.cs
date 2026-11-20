public static class MinMax {
  public static int MinMask(int x, int y) => y ^ ((x ^ y) & -(x < y ? 1 : 0));

  public static uint MaxMaskUnsigned(uint x, uint y) => x ^ ((x ^ y) & (x < y ? 0xFFFFFFFFu : 0u));

  public static int MinSelect(int x, int y) => x < y ? x : y;

  public static int MinSubtractSignBounded(ushort x, ushort y) {
    int d = x - y;
    return y + (d & (d >> 31));
  }

  public static int MaxSubtractSignBounded(ushort x, ushort y) {
    int d = x - y;
    return x - (d & (d >> 31));
  }

  public static int MinSubtractSign(int x, int y) {
    int d = x - y;
    return y + (d & (d >> 31));
  }

  public static int OtherMask(int x, int y, int k) => y ^ ((x ^ y) & -(x < k ? 1 : 0));

  public static int LogicalShift(ushort x, ushort y) {
    int d = x - y;
    return y + (d & (int)((uint)d >> 31));
  }

  public static int Checked(ushort x, ushort y) => y + (checked(x - y) & (checked(x - y) >> 31));
}
