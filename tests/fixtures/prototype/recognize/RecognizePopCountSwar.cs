// O0102: the SWAR population count of Hacker's Delight 5-1 on 32 and 64 bits, with and without
// the final multiplication, as statements and as one expression, and near misses. Written for this
// repository from the description in Warren, Hacker's Delight, 2nd ed., section 5-1.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizePopCountSwar {

  // expect: O0102
  public static int Multiply32(uint x) {
    x = x - ((x >> 1) & 0x55555555u);
    x = (x & 0x33333333u) + ((x >> 2) & 0x33333333u);
    x = (x + (x >> 4)) & 0x0F0F0F0Fu;
    return (int)((x * 0x01010101u) >> 24);
  }

  // expect: O0102
  public static int Shifts32(uint x) {
    x = x - ((x >> 1) & 0x55555555u);
    x = (x & 0x33333333u) + ((x >> 2) & 0x33333333u);
    x = (x + (x >> 4)) & 0x0F0F0F0Fu;
    x = x + (x >> 8);
    x = x + (x >> 16);
    return (int)(x & 0x3F);
  }

  // expect: O0102
  public static int Multiply64(ulong x) {
    x = x - ((x >> 1) & 0x5555555555555555UL);
    x = (x & 0x3333333333333333UL) + ((x >> 2) & 0x3333333333333333UL);
    x = (x + (x >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
    return (int)((x * 0x0101010101010101UL) >> 56);
  }

  // expect: O0102
  public static int Shifts64(ulong x) {
    x = x - ((x >> 1) & 0x5555555555555555UL);
    x = (x & 0x3333333333333333UL) + ((x >> 2) & 0x3333333333333333UL);
    x = (x + (x >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
    x = x + (x >> 8);
    x = x + (x >> 16);
    x = x + (x >> 32);
    return (int)(x & 0x7F);
  }

  // expect: O0102
  public static uint Assigned(uint x) {
    x = (x & 0x55555555u) + ((x >> 1) & 0x55555555u);
    x = (x & 0x33333333u) + ((x >> 2) & 0x33333333u);
    x = (x & 0x0F0F0F0Fu) + ((x >> 4) & 0x0F0F0F0Fu);
    x = (x * 0x01010101u) >> 24;
    return x;
  }

  // expect: O0102
  public static int Locals(ushort v) {
    uint x = v;
    uint a = x - ((x >> 1) & 0x5555u);
    uint b = (a & 0x3333u) + ((a >> 2) & 0x3333u);
    uint c = (b + (b >> 4)) & 0x0F0Fu;
    return (int)((c + (c >> 8)) & 0x1Fu);
  }

  // expect: -
  public static int CarryCrossesField(ushort v) {
    uint x = v;
    uint a = x - ((x >> 1) & 0x5555u);
    uint b = (a & 0x3333u) + ((a >> 2) & 0x3333u);
    return (int)(((b * 0x1111u) >> 12) & 0x1Fu);
  }

  // expect: -
  public static int WrongMask(uint x) {
    x = x - ((x >> 1) & 0x55555555u);
    x = (x & 0x33333333u) + ((x >> 2) & 0x13333333u);
    x = (x + (x >> 4)) & 0x0F0F0F0Fu;
    return (int)((x * 0x01010101u) >> 24);
  }

  // expect: -
  public static int MissingStep(uint x) {
    x = x - ((x >> 1) & 0x55555555u);
    x = (x + (x >> 4)) & 0x0F0F0F0Fu;
    return (int)((x * 0x01010101u) >> 24);
  }

  // expect: -
  public static uint StillRead(uint x) {
    uint y = x;
    x = x - ((x >> 1) & 0x55555555u);
    x = (x & 0x33333333u) + ((x >> 2) & 0x33333333u);
    x = (x + (x >> 4)) & 0x0F0F0F0Fu;
    y = (x * 0x01010101u) >> 24;
    return x + y;
  }

}
