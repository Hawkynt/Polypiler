// O0120: minimum and maximum from a comparison and selection, in every mirrored form, and from the
// branchless mask selection of Hacker's Delight 2-18; and near misses. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizeMinMax {

  // expect: O0120
  public static int Less(int a, int b) => a < b ? a : b;

  // expect: O0120
  public static int LessOrEqual(int a, int b) => a <= b ? a : b;

  // expect: O0120
  public static uint Greater(uint a, uint b) => a > b ? a : b;

  // expect: O0120
  public static long Swapped(long a, long b) => a < b ? b : a;

  // expect: O0120
  public static ulong Mirrored(ulong a, ulong b) => b > a ? a : b;

  // expect: O0120
  public static byte Bytes(byte a, byte b) => a < b ? a : b;

  // expect: O0120
  public static short Shorts(short a, short b) => a >= b ? a : b;

  // expect: O0120
  public static int Branchless(int a, int b) => b ^ ((a ^ b) & -(a < b ? 1 : 0));

  // expect: O0120
  public static int BranchlessMax(int a, int b) => a ^ ((a ^ b) & -(a < b ? 1 : 0));

  // expect: O0120
  public static uint BranchlessMask(uint a, uint b) => b ^ ((a ^ b) & (a < b ? 0xFFFFFFFFu : 0u));

  // expect: -
  public static int Different(int a, int b, int c) => a < b ? a : c;

  // expect: -
  public static double Doubles(double a, double b) => a < b ? a : b;

  // expect: -
  public static int Equality(int a, int b) => a == b ? a : b;

  // expect: -
  public static int FromArray(int[] values, int a) => values[0] < a ? values[0] : a;

  // expect: -
  public static int BranchlessWrong(int a, int b, int c) => b ^ ((a ^ c) & -(a < b ? 1 : 0));

}
