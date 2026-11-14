// O0121: the absolute value that wraps on the smallest value, from a selection and from the sign
// mask of Hacker's Delight 2-4; and near misses. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizeAbs {

  // expect: O0121
  public static int Select(int x) => x < 0 ? -x : x;

  // expect: O0121
  public static long SelectPositive(long x) => x >= 0 ? x : -x;

  // expect: O0121
  public static int SelectZero(int x) => 0 > x ? 0 - x : x;

  // expect: O0121
  public static int XorSubtract(int x) {
    int m = x >> 31;
    return (x ^ m) - m;
  }

  // expect: O0121
  public static long AddXor(long x) => (x + (x >> 63)) ^ (x >> 63);

  // expect: O0121
  public static int Short(short v) {
    int x = v;
    int m = x >> 31;
    return (x ^ m) - m;
  }

  // expect: -
  public static int Trapping(int x) => x < 0 ? checked(-x) : x;

  // expect: -
  public static int WrongMask(int x) {
    int m = x >> 30;
    return (x ^ m) - m;
  }

  // expect: -
  public static int Logical(int x) {
    int m = (int)((uint)x >> 31);
    return (x ^ m) - m;
  }

  // expect: -
  public static int Swapped(int x) => x < 0 ? x : -x;

  // expect: -
  public static int Library(int x) => System.Math.Abs(x);

}
