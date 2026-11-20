public static class Divide {
  public static uint Udiv10(uint x) => (uint)(((ulong)x * 0xCCCCCCCDUL) >> 35);

  public static uint Udiv7AddBack(uint x) {
    uint t = (uint)(((ulong)x * 0x24924925UL) >> 32);
    return (t + ((x - t) >> 1)) >> 2;
  }

  public static uint Udiv10Narrow(ushort x) => ((uint)x * 0xCCCDu) >> 19;

  public static int Sdiv10(int x) {
    int q = (int)(((long)x * 0x66666667L) >> 34);
    return q - (x >> 31);
  }

  public static uint Urem10(uint x) {
    uint q = (uint)(((ulong)x * 0xCCCCCCCDUL) >> 35);
    return x - q * 10;
  }

  public static ulong Udiv3Wide(ulong x) {
    ulong h = System.Math.BigMul(x, 0xAAAAAAAAAAAAAAABUL, out ulong low);
    return h >> 1;
  }

  public static uint Udiv10BadMagic(uint x) => (uint)(((ulong)x * 0xCCCCCCCCUL) >> 35);

  public static uint Udiv10NarrowMul(uint x) => (x * 0xCCCDu) >> 19;

  public static int Sdiv10Uncorrected(int x) => (int)(((long)x * 0x66666667L) >> 34);

  public static uint Urem10By9(uint x) {
    uint q = (uint)(((ulong)x * 0xCCCCCCCDUL) >> 35);
    return x - q * 9;
  }
}
