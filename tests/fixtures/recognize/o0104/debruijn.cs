public static class DeBruijn {
  private static readonly byte[] Trailing32 = { 0, 1, 28, 2, 29, 14, 24, 3, 30, 22, 20, 15, 25, 17, 4, 8, 31, 27, 13, 23, 21, 19, 16, 7, 26, 12, 18, 6, 11, 5, 10, 9 };

  private static readonly int[] Log32 = { 0, 9, 1, 10, 13, 21, 2, 29, 11, 14, 16, 18, 22, 25, 3, 30, 8, 12, 20, 28, 15, 17, 24, 7, 19, 27, 23, 6, 26, 5, 4, 31 };

  private static readonly byte[] Trailing64 = {
    0, 1, 48, 2, 57, 49, 28, 3, 61, 58, 50, 42, 38, 29, 17, 4, 62, 55, 59, 36, 53, 51, 43, 22, 45, 39, 33, 30, 24, 18, 12, 5,
    63, 47, 56, 27, 60, 41, 37, 16, 54, 35, 52, 21, 44, 32, 23, 11, 46, 26, 40, 15, 34, 20, 31, 10, 25, 14, 19, 9, 13, 8, 7, 6 };

  private static readonly byte[] Log64 = {
    0, 47, 1, 56, 48, 27, 2, 60, 57, 49, 41, 37, 28, 16, 3, 61, 54, 58, 35, 52, 50, 42, 21, 44, 38, 32, 29, 23, 17, 11, 4, 62,
    46, 55, 26, 59, 40, 36, 15, 53, 34, 51, 20, 43, 31, 22, 10, 45, 25, 39, 14, 33, 19, 30, 9, 24, 13, 18, 8, 12, 7, 6, 5, 63 };

  // Entry 2 changed from 28 to 27.
  private static readonly byte[] BadTable = { 0, 1, 27, 2, 29, 14, 24, 3, 30, 22, 20, 15, 25, 17, 4, 8, 31, 27, 13, 23, 21, 19, 16, 7, 26, 12, 18, 6, 11, 5, 10, 9 };

  private static readonly byte[] Written = { 0, 1, 28, 2, 29, 14, 24, 3, 30, 22, 20, 15, 25, 17, 4, 8, 31, 27, 13, 23, 21, 19, 16, 7, 26, 12, 18, 6, 11, 5, 10, 9 };

  public static int TrailingZeros(uint x) => Trailing32[((x & (uint)-x) * 0x077CB531u) >> 27];

  public static int FloorLog2(uint v) {
    v |= v >> 1;
    v |= v >> 2;
    v |= v >> 4;
    v |= v >> 8;
    v |= v >> 16;
    return Log32[(v * 0x07C4ACDDu) >> 27];
  }

  // The C# frontend takes array indices of at most 32 bits, so the 64-bit index is converted.
  public static int TrailingZeros64(ulong x) => Trailing64[(int)(((x & (ulong)-(long)x) * 0x03F79D71B4CB0A89UL) >> 58)];

  public static int FloorLog64(ulong v) {
    v |= v >> 1;
    v |= v >> 2;
    v |= v >> 4;
    v |= v >> 8;
    v |= v >> 16;
    v |= v >> 32;
    return Log64[(int)((v * 0x03F79D71B4CB0A89UL) >> 58)];
  }

  public static int WrongEntry(uint x) => BadTable[((x & (uint)-x) * 0x077CB531u) >> 27];

  public static int WrittenTable(uint x) => Written[((x & (uint)-x) * 0x077CB531u) >> 27];

  public static void Overwrite() => Written[2] = 27;

  public static int ClearsLowest(uint x) => Trailing32[((x & (x - 1)) * 0x077CB531u) >> 27];

  public static int WrongShift(uint x) => Trailing32[((x & (uint)-x) * 0x077CB531u) >> 28];
}
