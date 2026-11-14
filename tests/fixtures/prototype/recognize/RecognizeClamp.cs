// O0123: a value kept between constant bounds, from nested minimum and maximum and from the
// compare-select chain; and near misses. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizeClamp {

  // expect: O0123
  public static int Nested(int x) => System.Math.Min(System.Math.Max(x, 0), 255);

  // expect: O0123
  public static int NestedOther(int x) => System.Math.Max(System.Math.Min(x, 100), -100);

  // expect: O0123
  public static long ConstantsFirst(long x) => System.Math.Min(1000L, System.Math.Max(-1000L, x));

  // expect: O0123
  public static int Chain(int x) => x < 0 ? 0 : x > 255 ? 255 : x;

  // expect: O0123
  public static int ChainUpperFirst(int x) => x > 255 ? 255 : x < 0 ? 0 : x;

  // expect: O0123
  public static uint ChainMin(uint x) => x < 10u ? 10u : System.Math.Min(x, 20u);

  // expect: O0123
  public static short Shorts(short x) => x < -10 ? (short)-10 : x > 10 ? (short)10 : x;

  // expect: O0123
  public static byte Bytes(byte x) => x <= 16 ? (byte)16 : x >= 235 ? (byte)235 : x;

  // expect: O0120
  public static int OutOfOrder(int x) => System.Math.Min(System.Math.Max(x, 255), 0) + (x < 255 ? 255 : x > 0 ? 0 : x);

  // expect: -
  public static int VariableBound(int x, int high) => System.Math.Min(System.Math.Max(x, 0), high);

  // expect: -
  public static int SameDirection(int x) => System.Math.Min(System.Math.Min(x, 0), 255);

}
