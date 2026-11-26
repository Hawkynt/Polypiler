// Showcase: Collections
// Category: Collections & Sequences
// Summary: List<T>, Dictionary<TKey, TValue>, and HashSet<T> mutations, lookups, and iteration.
using System.Collections.Generic;

public static class CollectionsShowcase {
  public static int Run(int[] values) {
    var list = new List<int>();
    var seen = new HashSet<int>();
    var counts = new Dictionary<string, int>();

    foreach (var v in values) {
      if (seen.Add(v))
        list.Add(v);
      string bucket = v >= 0 ? "pos" : "neg";
      if (counts.TryGetValue(bucket, out var current))
        counts[bucket] = current + 1;
      else
        counts.Add(bucket, 1);
    }

    int pos = 0;
    if (counts.TryGetValue("pos", out var p))
      pos = p;
    return list.Count * 10 + pos;
  }
}
