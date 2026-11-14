// O0127: a quotient and a remainder of the same operands in one block, and near misses. The
// expectations assume a target whose capability table prefers the remainder from the quotient.
// Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizeQuotient {

  // expect: O0127
  public static int Both(int a, int b) {
    int q = a / b;
    int r = a % b;
    return q * 1000 + r;
  }

  // expect: O0127
  public static ulong Unsigned(ulong a, ulong b) {
    ulong q = a / b;
    ulong sum = q;
    return sum + a % b;
  }

  // expect: O0127
  public static long Assigned(long a, long b, long q) {
    q = a / b;
    return q - (a % b);
  }

  // expect: -
  public static int Changed(int a, int b) {
    int q = a / b;
    a = a + 1;
    return q + a % b;
  }

  // expect: -
  public static int Other(int a, int b, int c) {
    int q = a / b;
    return q + a % c;
  }

  // expect: -
  public static int Before(int a, int b) {
    int r = a % b;
    int q = a / b;
    return q + r;
  }

  // expect: -
  public static int Nested(int a, int b) {
    int q = a / b;
    if (q > 0)
      return a % b;

    return q;
  }

}
