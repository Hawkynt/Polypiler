// Euclid's greatest common divisor, the Fibonacci numbers modulo 2^32 and a 32-bit rotation.
// Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class Numbers {

  public static int Gcd(int a, int b) {
    while (b != 0) {
      int remainder = a % b;
      a = b;
      b = remainder;
    }

    return a;
  }

  public static uint Fibonacci(int n) {
    uint current = 0;
    uint next = 1;
    for (int i = 0; i < n; ++i) {
      uint sum = current + next;
      current = next;
      next = sum;
    }

    return current;
  }

  public static uint RotateLeft(uint value, int count) => (value << count) | (value >> (32 - count));

}
