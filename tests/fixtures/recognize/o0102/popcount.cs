public static class PopCount {
  public static uint Multiply32(uint x) {
    x = x - ((x >> 1) & 0x55555555u);
    x = (x & 0x33333333u) + ((x >> 2) & 0x33333333u);
    x = (x + (x >> 4)) & 0x0F0F0F0Fu;
    return (x * 0x01010101u) >> 24;
  }

  public static uint Fold32(uint x) {
    x = (x & 0x55555555u) + ((x >> 1) & 0x55555555u);
    x = (x & 0x33333333u) + ((x >> 2) & 0x33333333u);
    x = (x + (x >> 4)) & 0x0F0F0F0Fu;
    x = x + (x >> 8);
    x = x + (x >> 16);
    return x & 0x3Fu;
  }

  public static ulong Multiply64(ulong x) {
    x = x - ((x >> 1) & 0x5555555555555555UL);
    x = (x & 0x3333333333333333UL) + ((x >> 2) & 0x3333333333333333UL);
    x = (x + (x >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
    return (x * 0x0101010101010101UL) >> 56;
  }

  public static uint Narrow16(ushort v) {
    uint x = v;
    uint a = x - ((x >> 1) & 0x5555u);
    uint b = (a & 0x3333u) + ((a >> 2) & 0x3333u);
    uint c = (b + (b >> 4)) & 0x0F0Fu;
    return (c + (c >> 8)) & 0x1Fu;
  }

  public static uint BadMask32(uint x) {
    x = x - ((x >> 1) & 0x55555555u);
    x = (x & 0x33333333u) + ((x >> 2) & 0x33333332u);
    x = (x + (x >> 4)) & 0x0F0F0F0Fu;
    return (x * 0x01010101u) >> 24;
  }

  public static ulong U64WithU32Masks(ulong x) {
    x = x - ((x >> 1) & 0x55555555UL);
    x = (x & 0x33333333UL) + ((x >> 2) & 0x33333333UL);
    x = (x + (x >> 4)) & 0x0F0F0F0FUL;
    return (x * 0x01010101UL) >> 24;
  }

  public static uint Shift16(uint x) {
    x = x - ((x >> 1) & 0x55555555u);
    x = (x & 0x33333333u) + ((x >> 2) & 0x33333333u);
    x = (x + (x >> 4)) & 0x0F0F0F0Fu;
    return (x * 0x01010101u) >> 16;
  }

  public static uint TwoVariables(uint x, uint y) {
    x = x - ((y >> 1) & 0x55555555u);
    x = (x & 0x33333333u) + ((x >> 2) & 0x33333333u);
    x = (x + (x >> 4)) & 0x0F0F0F0Fu;
    return (x * 0x01010101u) >> 24;
  }
}
