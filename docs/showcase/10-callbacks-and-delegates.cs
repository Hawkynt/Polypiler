// Showcase: Callbacks & Delegates
// Category: Functional
// Summary: Higher-order functions accepting Func<T, T> and custom delegates via lambdas and method groups.
using System;

public delegate int IntTransform(int value);

public static class CallbacksShowcase {
  private static int Square(int x) => x * x;

  private static int ApplyTwice(int value, IntTransform transform)
    => transform(transform(value));

  private static int MapSum(int[] values, Func<int, int> mapper) {
    int sum = 0;
    for (int i = 0; i < values.Length; ++i)
      sum += mapper(values[i]);
    return sum;
  }

  public static int Run(int[] values, int seed)
    => ApplyTwice(seed, Square) + MapSum(values, x => x + 1);
}
