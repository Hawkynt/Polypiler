// Showcase: Lambdas & Closures
// Category: Functional
// Summary: Lambda expressions and local functions capturing and mutating shared outer locals.
using System;

public static class ClosuresShowcase {
  public static int Run(int start, int step) {
    int acc = start;
    Func<int, int> bump = delta => {
      acc += delta;
      return acc;
    };

    int DoubleAcc() {
      acc *= 2;
      return acc;
    }

    int first = bump(step);
    int second = DoubleAcc();
    return first + second;
  }
}
