// Executable tree-IR fixtures for #557; the cascade identity is Hacker's Delight, section 7-1.
public static class Reversal {
  public static uint Cascade(uint x) {
    x = ((x >> 1) & 0x55555555u) | ((x & 0x55555555u) << 1);
    x = ((x >> 2) & 0x33333333u) | ((x & 0x33333333u) << 2);
    x = ((x >> 4) & 0x0F0F0F0Fu) | ((x & 0x0F0F0F0Fu) << 4);
    x = ((x >> 8) & 0x00FF00FFu) | ((x & 0x00FF00FFu) << 8);
    return (x >> 16) | (x << 16);
  }

  public static uint RotateLast(uint x) {
    x = ((x >> 1) & 0x55555555u) | ((x & 0x55555555u) << 1);
    x = ((x >> 2) & 0x33333333u) | ((x & 0x33333333u) << 2);
    x = ((x >> 4) & 0x0F0F0F0Fu) | ((x & 0x0F0F0F0Fu) << 4);
    x = ((x >> 8) & 0x00FF00FFu) | ((x & 0x00FF00FFu) << 8);
    return System.Numerics.BitOperations.RotateLeft(x, 16);
  }

  public static uint MissingHalves(uint x) {
    x = ((x >> 1) & 0x55555555u) | ((x & 0x55555555u) << 1);
    x = ((x >> 2) & 0x33333333u) | ((x & 0x33333333u) << 2);
    x = ((x >> 4) & 0x0F0F0F0Fu) | ((x & 0x0F0F0F0Fu) << 4);
    return ((x >> 8) & 0x00FF00FFu) | ((x & 0x00FF00FFu) << 8);
  }

  public static uint Loop(uint x) {
    uint r = 1;
    for (int i = 0; i < 32; ++i) {
      r = (r << 1) | (x & 1);
      x >>= 1;
    }
    return r;
  }
}
