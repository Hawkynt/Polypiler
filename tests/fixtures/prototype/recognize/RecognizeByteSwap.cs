// O0106: byte swaps of 16, 32 and 64 bits assembled from shifts and masks, in one expression and
// as a two-step swap; and near misses. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizeByteSwap {

  // expect: O0106
  public static ushort Swap16(ushort x) => (ushort)((x >> 8) | (x << 8));

  // expect: O0106
  public static uint Swap32(uint x) => (x >> 24) | ((x >> 8) & 0x0000FF00u) | ((x << 8) & 0x00FF0000u) | (x << 24);

  // expect: O0106
  public static uint Swap32Masks(uint x) => ((x & 0x000000FFu) << 24) | ((x & 0x0000FF00u) << 8) | ((x & 0x00FF0000u) >> 8) | ((x & 0xFF000000u) >> 24);

  // expect: O0106
  public static int Signed32(int x) => (int)(((uint)x >> 24) | (((uint)x >> 8) & 0xFF00u) | (((uint)x << 8) & 0xFF0000u) | ((uint)x << 24));

  // expect: O0106
  public static ulong Swap64(ulong x) => (x >> 56) | ((x >> 40) & 0xFF00UL) | ((x >> 24) & 0xFF0000UL) | ((x >> 8) & 0xFF000000UL)
    | ((x << 8) & 0xFF00000000UL) | ((x << 24) & 0xFF0000000000UL) | ((x << 40) & 0xFF000000000000UL) | (x << 56);

  // expect: O0106
  public static uint TwoSteps(uint x) {
    x = ((x & 0xFF00FF00u) >> 8) | ((x & 0x00FF00FFu) << 8);
    x = (x >> 16) | (x << 16);
    return x;
  }

  // expect: O0106
  public static uint ByRotations(uint x) => (((x << 8) | (x >> 24)) & 0x00FF00FFu) | (((x << 24) | (x >> 8)) & 0xFF00FF00u);

  // expect: -
  public static uint WrongOrder(uint x) => (x >> 24) | ((x >> 8) & 0x0000FF00u) | ((x << 8) & 0x00FF0000u) | ((x << 16) & 0xFF000000u);

  // expect: -
  public static uint MissingByte(uint x) => (x >> 24) | ((x >> 8) & 0x0000FF00u) | (x << 24);

  // expect: -
  public static int ArithmeticShift(int x) => (x >> 24) | ((x >> 8) & 0xFF00) | ((x << 8) & 0xFF0000) | (x << 24);

}
