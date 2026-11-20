public static class Parity {
  public static int Fold32(uint x) {
    x ^= x >> 16;
    x ^= x >> 8;
    x ^= x >> 4;
    x ^= x >> 2;
    x ^= x >> 1;
    return (int)(x & 1);
  }

  public static int Nibble8(byte x) => (0x6996 >> ((x ^ (x >> 4)) & 0xF)) & 1;

  public static int Nibble32(uint x) {
    x ^= x >> 16;
    x ^= x >> 8;
    x ^= x >> 4;
    return (0x6996 >> (int)(x & 0xF)) & 1;
  }

  public static int Signed(int x) {
    x ^= x >> 16;
    x ^= x >> 8;
    x ^= x >> 4;
    x ^= x >> 2;
    x ^= x >> 1;
    return x & 1;
  }

  public static int SkipsAStep(uint x) {
    x ^= x >> 16;
    x ^= x >> 8;
    x ^= x >> 2;
    x ^= x >> 1;
    return (int)(x & 1);
  }

  public static int ComplementTable(byte x) => (0x9669 >> ((x ^ (x >> 4)) & 0xF)) & 1;

  public static int MaskThree(uint x) {
    x ^= x >> 16;
    x ^= x >> 8;
    x ^= x >> 4;
    x ^= x >> 2;
    x ^= x >> 1;
    return (int)(x & 3);
  }
}
