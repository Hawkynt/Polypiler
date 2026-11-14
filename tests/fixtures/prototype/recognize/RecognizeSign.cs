// O0122: the sign of an integer from comparison differences and from the shift-or of Hacker's
// Delight 2-7; and near misses. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizeSign {

  // expect: O0122
  public static int Compared(int x) => (x > 0 ? 1 : 0) - (x < 0 ? 1 : 0);

  // expect: O0122
  public static int ComparedMirrored(long x) => (0 < x ? 1 : 0) - (0 > x ? 1 : 0);

  // expect: O0122
  public static int Shifted(int x) => (x >> 31) | (int)((uint)-x >> 31);

  // expect: O0122
  public static long Shifted64(long x) => (x >> 63) | (long)((ulong)-x >> 63);

  // expect: O0122
  public static int Bytes(sbyte v) => (v > 0 ? 1 : 0) - (v < 0 ? 1 : 0);

  // expect: -
  public static int Swapped(int x) => (x < 0 ? 1 : 0) - (x > 0 ? 1 : 0);

  // expect: -
  public static int TwoValues(int x, int y) => (x > 0 ? 1 : 0) - (y < 0 ? 1 : 0);

  // expect: -
  public static int NotNegated(int x) => (x >> 31) | (int)((uint)x >> 31);

  // expect: -
  public static int Arithmetic(int x) => (x >> 31) | (-x >> 31);

}
