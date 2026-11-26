// Showcase: Iterators & Lazy Sequences
// Category: Collections & Sequences
// Summary: Restartable iterator methods with yield return and yield break consumed lazily via foreach.
using System.Collections.Generic;

public static class IteratorsShowcase {
  private static IEnumerable<int> EvensUpTo(int limit) {
    for (int i = 0; i <= limit; i += 2) {
      if (i > 100)
        yield break;
      yield return i;
    }
  }

  public static int SumEvens(int limit) {
    int sum = 0;
    foreach (var value in EvensUpTo(limit))
      sum += value;
    return sum;
  }
}
