// Showcase: LINQ
// Category: Collections & Sequences
// Summary: Deferred LINQ query pipelines combining Where, Select, Take, and Sum.
using System.Linq;

public static class LinqShowcase {
  public static int TopSquaredSum(int[] values, int count)
    => values
      .Where(x => x > 0)
      .Select(x => x * x)
      .Take(count)
      .Sum();
}
