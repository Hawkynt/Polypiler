// O0105: bit reversal as the swap cascade of Hacker's Delight 7-1 on 32 and 64 bits, as one
// expression, and as a loop that moves one bit per pass; and near misses. Written for this
// repository from Warren, Hacker's Delight, 2nd ed., section 7-1.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizeBitReverse {

  // expect: O0105
  public static uint Cascade32(uint x) {
    x = ((x >> 1) & 0x55555555u) | ((x & 0x55555555u) << 1);
    x = ((x >> 2) & 0x33333333u) | ((x & 0x33333333u) << 2);
    x = ((x >> 4) & 0x0F0F0F0Fu) | ((x & 0x0F0F0F0Fu) << 4);
    x = ((x >> 8) & 0x00FF00FFu) | ((x & 0x00FF00FFu) << 8);
    x = (x >> 16) | (x << 16);
    return x;
  }

  // expect: O0105
  public static ulong Cascade64(ulong x) {
    x = ((x >> 1) & 0x5555555555555555UL) | ((x & 0x5555555555555555UL) << 1);
    x = ((x >> 2) & 0x3333333333333333UL) | ((x & 0x3333333333333333UL) << 2);
    x = ((x >> 4) & 0x0F0F0F0F0F0F0F0FUL) | ((x & 0x0F0F0F0F0F0F0F0FUL) << 4);
    x = ((x >> 8) & 0x00FF00FF00FF00FFUL) | ((x & 0x00FF00FF00FF00FFUL) << 8);
    x = ((x >> 16) & 0x0000FFFF0000FFFFUL) | ((x & 0x0000FFFF0000FFFFUL) << 16);
    x = (x >> 32) | (x << 32);
    return x;
  }

  // expect: O0105
  public static byte Byte(byte b) {
    uint x = b;
    x = ((x >> 1) & 0x55u) | ((x & 0x55u) << 1);
    x = ((x >> 2) & 0x33u) | ((x & 0x33u) << 2);
    x = ((x >> 4) & 0x0Fu) | ((x & 0x0Fu) << 4);
    return (byte)x;
  }

  // expect: O0105
  public static ushort Short(ushort v) => (ushort)(((v & 1) << 15) | ((v & 2) << 13) | ((v & 4) << 11) | ((v & 8) << 9) | ((v & 16) << 7) | ((v & 32) << 5)
    | ((v & 64) << 3) | ((v & 128) << 1) | ((v >> 1) & 128) | ((v >> 3) & 64) | ((v >> 5) & 32) | ((v >> 7) & 16) | ((v >> 9) & 8) | ((v >> 11) & 4)
    | ((v >> 13) & 2) | ((v >> 15) & 1));

  // expect: O0105
  public static uint Loop(uint x) {
    uint r = 0;
    for (int i = 0; i < 32; i++) {
      r = (r << 1) | (x & 1);
      x >>= 1;
    }

    return r;
  }

  // expect: O0105
  public static ushort LoopShort(ushort v) {
    int r = 0;
    int x = v;
    for (int i = 0; i < 16; i++) {
      r = (r << 1) | (x & 1);
      x >>= 1;
    }

    return (ushort)(r + x);
  }

  // expect: -
  public static uint MissingStep(uint x) {
    x = ((x >> 1) & 0x55555555u) | ((x & 0x55555555u) << 1);
    x = ((x >> 4) & 0x0F0F0F0Fu) | ((x & 0x0F0F0F0Fu) << 4);
    x = ((x >> 8) & 0x00FF00FFu) | ((x & 0x00FF00FFu) << 8);
    return x;
  }

  // expect: -
  public static uint LoopTooShort(uint x) {
    uint r = 0;
    for (int i = 0; i < 31; i++) {
      r = (r << 1) | (x & 1);
      x >>= 1;
    }

    return r;
  }

  // expect: -
  public static uint LoopKeepsOld(uint x, uint r) {
    for (int i = 0; i < 16; i++) {
      r = (r << 1) | (x & 1);
      x >>= 1;
    }

    return r;
  }

}
