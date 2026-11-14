// O0125: multiplication by a constant written as shifts and adds, wrapping only; and near misses.
// Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizeMultiply {

  // expect: O0125
  public static int Ten(int x) => (x << 3) + (x << 1);

  // expect: O0125
  public static uint Fifteen(uint x) => (x << 4) - x;

  // expect: O0125
  public static long Chain(long x) => (x << 5) + (x << 3) - (x << 1) + x;

  // expect: O0125
  public static uint Jenkins(uint h) {
    h += h << 10;
    return h;
  }

  // expect: O0125
  public static int FromByte(byte b) => (b << 2) + b;

  // expect: O0125
  public static int FromShort(short s) => (s << 4) - (s << 1);

  // expect: -
  public static int PowerOfTwo(int x) => (x << 2) + (x << 2);

  // expect: -
  public static int TwoValues(int x, int y) => (x << 3) + (y << 1);

  // expect: -
  public static int Checked(int x) => checked((x << 3) + (x << 1));

  // expect: O0125
  public static int WithConstant(int x) => (x << 3) + (x << 1) + 1;

  // expect: -
  public static int Negation(int x) => 0 - x;

}
