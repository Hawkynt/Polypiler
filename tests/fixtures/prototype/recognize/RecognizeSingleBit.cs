// O0108: single-bit test, set, clear and toggle with a variable or constant position, and near
// misses. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizeSingleBit {

  // expect: O0108
  public static bool TestShifted(uint x, int i) => ((x >> i) & 1) != 0;

  // expect: O0108
  public static bool TestMask(ulong x, int i) => (x & (1UL << i)) != 0;

  // expect: O0108
  public static bool TestClear(int x, int i) => ((x >> i) & 1) == 0;

  // expect: O0108
  public static bool TestOne(long x, int i) => ((x >> i) & 1) == 1;

  // expect: O0108
  public static bool TestConstant(uint x) => ((x >> 7) & 1) != 0;

  // expect: O0108
  public static bool TestMasked(uint x, int i) => ((x >> (i & 31)) & 1) != 0;

  // expect: O0108
  public static uint Set(uint x, int i) => x | (1u << i);

  // expect: O0108
  public static ulong Clear(ulong x, int i) => x & ~(1UL << i);

  // expect: O0108
  public static int Toggle(int x, int i) => x ^ (1 << i);

  // expect: O0108
  public static uint SetFirst(uint x, int i) => (1u << i) | x;

  // expect: -
  public static bool TwoBits(uint x, int i) => ((x >> i) & 3) != 0;

  // expect: -
  public static bool MaskOne(uint x, int i) => (x & (1u << i)) == 1;

  // expect: O0108
  public static bool Narrow(ushort x, int i) => ((x >> i) & 1) != 0;

  // expect: -
  public static uint SetTwo(uint x, int i) => x | (3u << i);

  // expect: -
  public static long WidenedBit(long x, int i) => x | (long)(uint)(1 << i);

  // expect: -
  public static uint SetFromArray(uint[] a, int i) => a[0] | (1u << i);

}
