// Executable tree-IR fixtures for #559; masks name byte positions, not a likely source spelling.
public static class ByteSwap {
  public static uint Terms(uint x) => (x >> 24) | ((x >> 8) & 0xFF00u) | ((x << 8) & 0xFF0000u) | (x << 24);

  public static uint Cascade(uint x) {
    x = ((x >> 8) & 0x00FF00FFu) | ((x & 0x00FF00FFu) << 8);
    return System.Numerics.BitOperations.RotateLeft(x, 16);
  }

  public static ulong Loop(ulong x) {
    ulong r = 0;
    for (int i = 0; i < 8; ++i) {
      r = (r << 8) | (x & 255);
      x >>= 8;
    }
    return r;
  }

  public static int SignCopies(int x) => (x >> 24) | ((x >> 8) & 0xFF00) | ((x << 8) & 0xFF0000) | (x << 24);
  public static uint WordSwap(uint x) => (x >> 16) | (x << 16);
}
