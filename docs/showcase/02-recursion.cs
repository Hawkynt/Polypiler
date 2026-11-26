// Showcase: Recursion
// Category: Control Flow & Functions
// Summary: Direct recursive Euclidean GCD and recursive Fibonacci with base cases.
public static class RecursionShowcase {
  public static int Gcd(int a, int b) {
    if (b == 0)
      return a < 0 ? -a : a;
    return Gcd(b, a % b);
  }

  public static int Fib(int n) {
    if (n <= 1)
      return n;
    return Fib(n - 1) + Fib(n - 2);
  }
}
