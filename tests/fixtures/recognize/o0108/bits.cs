public static class Bits {
  public static bool TestShift(uint x, int n) => ((x >> n) & 1) != 0;

  public static bool TestMask(uint x, int n) => (x & (1u << n)) != 0;

  public static bool TestArithmetic(int x, int n) => ((x >> n) & 1) != 0;

  public static uint Set(uint x, int n) => x | (1u << n);

  public static uint Clear(uint x, int n) => x & ~(1u << n);

  public static uint Flip(uint x, int n) => x ^ (1u << n);

  public static byte SetByte(byte x, int n) => (byte)(x | (1 << n));

  public static ulong SetNarrowOne(ulong x, int n) => x | (ulong)(1 << n);

  public static uint SetTwo(uint x, int n) => x | (2u << n);

  public static bool Field(uint x, int n) => ((x >> n) & 3) != 0;
}
