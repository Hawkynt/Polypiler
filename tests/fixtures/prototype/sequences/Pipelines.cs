// LINQ to Objects: deferred execution, the order of the calls of the lambdas, re-enumeration, late
// captures, checked and single-precision sums, stable ordering, the terminals of an empty sequence,
// Range, Take and Skip at their edges, Zip, query syntax and the array searches of System.Array. Every
// lambda that matters records its calls in a list, so that the order of the calls is compared, not
// only the result. Values are the ones .NET 10 computes. Written for this repository.
#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Fixtures.Prototype.Sequences;

public static class Pipelines {

  private static IEnumerable<int> Naturals() {
    var n = 0;
    while (true) {
      yield return n;
      ++n;
    }
  }

  // Nothing runs before the sequence is enumerated, and First stops at the first match: 3 (built),
  // then 1 2 for the element 1 and 1 2 for the element 2, then the result 4.
  public static int Deferred() {
    var log = 0;
    var a = new[] { 1, 2, 3, 4 };
    var q = a.Select(x => {
      log = log * 10 + 1;
      return x * 2;
    }).Where(x => {
      log = log * 10 + 2;
      return x > 2;
    });
    log = log * 10 + 3;
    var first = q.First();
    return log * 10 + first;
  }

  // Element by element through the chain: x, then -10x.
  public static int[] Trace() {
    var log = new List<int>();
    var result = new[] { 1, 2, 3, 4 }.Select(x => {
      log.Add(x);
      return x * 10;
    }).Where(y => {
      log.Add(-y);
      return true;
    }).ToArray();
    return log.ToArray();
  }

  // A sequence held in a variable is enumerated twice: the lambda runs once per element each time.
  public static int Recount() {
    var calls = 0;
    var q = new[] { 1, 2, 3, 4 }.Select(x => {
      ++calls;
      return x;
    });
    var count = q.Count();
    var sum = q.Sum();
    return calls * 100 + count * 10 + sum;
  }

  // A captured variable is read when the sequence is enumerated, not when it is built.
  public static int[] Late() {
    var k = 2;
    var q = new[] { 1, 2, 3, 4 }.Where(x => x > k);
    k = 3;
    return q.ToArray();
  }

  public static int SumOf(int[] a) {
    try {
      return a.Sum();
    } catch (OverflowException) {
      return -1;
    }
  }

  public static long SumOfLongs(long[] a) {
    try {
      return a.Sum();
    } catch (OverflowException) {
      return -1;
    }
  }

  public static float SumOfSingles(float[] a) => a.Sum();

  public static double SumOfDoubles(double[] a) => a.Sum();

  // The keys sort stably: the values of equal keys keep their order.
  public static int[] Stable(int[] keys, int[] values) => Enumerable.Range(0, keys.Length).OrderBy(i => keys[i]).Select(i => values[i]).ToArray();

  public static double[] SortedDoubles(double[] a) => a.OrderBy(x => x).ToArray();

  public static int[] Descending(int[] a) => a.OrderByDescending(x => x / 10).ThenBy(x => x % 10).ToArray();

  // The keys of an ordering are computed once per element, level by level, after every element was pulled.
  public static int[] KeyOrder() {
    var log = new List<int>();
    var sorted = new[] { 3, 1, 2 }.Select(x => {
      log.Add(x);
      return x;
    }).OrderBy(x => {
      log.Add(10 * x);
      return x;
    }).ThenByDescending(x => {
      log.Add(100 * x);
      return -x;
    }).ToArray();
    foreach (var x in sorted)
      log.Add(-x);

    return log.ToArray();
  }

  public static int FirstOf(int[] a) {
    try {
      return a.First();
    } catch (InvalidOperationException) {
      return -1;
    }
  }

  public static int FirstEven(int[] a) {
    try {
      return a.First(x => x % 2 == 0);
    } catch (InvalidOperationException) {
      return -1;
    }
  }

  // The terminals of one array: Any, All, Count, FirstOrDefault, Min, Max and the folds, each a digit or a value.
  public static int[] Terminals(int[] a) {
    var min = -1;
    var max = -1;
    var folded = -1;
    try {
      min = a.Min();
      max = a.Max();
      folded = a.Aggregate((s, x) => s * 2 + x);
    } catch (InvalidOperationException) {
      min = -2;
    }

    return new[] {
      a.Any() ? 1 : 0, a.Any(x => x > 2) ? 1 : 0, a.All(x => x > 0) ? 1 : 0, a.Count(), a.Count(x => x % 2 == 1),
      a.FirstOrDefault(), a.FirstOrDefault(x => x > 2), min, max, folded, a.Aggregate(7, (s, x) => s - x), a.Contains(3) ? 1 : 0
    };
  }

  public static double[] Extremes(double[] a) => new[] { a.Min(), a.Max() };

  public static int[] Window(int skip, int take) => Enumerable.Range(1, 10).Skip(skip).Take(take).ToArray();

  // Range traps when it is called, before anything enumerates it.
  public static int RangeOf(int start, int count) {
    try {
      var r = Enumerable.Range(start, count);
      return -2;
    } catch (ArgumentOutOfRangeException) {
      return -1;
    }
  }

  // Take stops pulling an infinite sequence.
  public static int[] Infinite() => Naturals().Select(x => x * 2).Take(3).ToArray();

  // Take stops pulling after the n-th element: the selector runs n times.
  public static int TakeCalls(int n) {
    var calls = 0;
    var taken = new[] { 1, 2, 3, 4, 5 }.Select(x => {
      ++calls;
      return x;
    }).Take(n).Sum();
    return calls * 100 + taken;
  }

  // First stops before the element whose lambda would trap.
  public static int LazyTrap() => new[] { 1, 2, 3, 4 }.Select(x => 10 / (3 - x)).First();

  public static int[] Zipped(int[] a, int[] b) => a.Zip(b, (x, y) => x * 10 + y).ToArray();

  public static int[] ZipTrace() {
    var log = new List<int>();
    var first = new[] { 1, 2, 3 }.Select(x => {
      log.Add(x);
      return x;
    });
    var second = new List<int> { 7, 8 };
    var zipped = first.Zip(second, (x, y) => {
      log.Add(x * 10 + y);
      return x;
    }).ToArray();
    return log.ToArray();
  }

  public static int[] Flattened() {
    var log = new List<int>();
    var result = new[] { 1, 2, 3 }.SelectMany(x => {
      log.Add(-x);
      return Enumerable.Range(0, x);
    }).Where(y => {
      log.Add(y);
      return true;
    }).Take(4).ToArray();
    foreach (var x in result)
      log.Add(x);

    return log.ToArray();
  }

  public static int[] Pairs() => new[] { 1, 2 }.SelectMany(x => new[] { 10, 20 }, (x, y) => x + y).ToArray();

  public static int[] Indexed() => new[] { 5, 6, 7, 8 }.Where((x, i) => i % 2 == 0).Select((x, i) => x * 10 + i).ToArray();

  public static int[] Query(int[] a) => (from x in a where x > 1 orderby x descending select x * x).ToArray();

  // A let and a second from: the rest of the query runs once per element and per inner element.
  public static int[] Nested() {
    var log = new List<int>();
    var q = from x in new[] { 1, 2 }
            let y = Logged(log, x * 10)
            from z in new[] { 3, 4 }
            where Logged(log, z) > 3
            select x + y + z;
    foreach (var x in q.ToArray())
      log.Add(x);

    return log.ToArray();
  }

  private static int Logged(List<int> log, int value) {
    log.Add(value);
    return value;
  }

  // A sequence returned and enumerated by the caller.
  private static IEnumerable<int> Evens(IEnumerable<int> s) {
    foreach (var x in s.Where(v => v % 2 == 0))
      yield return x;
  }

  public static int Escaped() => Evens(Enumerable.Range(1, 9)).Select(x => x * x).Sum();

  public static int IndexOfNaN() => Array.IndexOf(new[] { double.NaN }, double.NaN);

  public static int[] Searches(int[] a) => new[] {
    Array.IndexOf(a, 3), Array.Find(a, x => x > 2), Array.FindIndex(a, x => x > 2), Array.Exists(a, x => x < 0) ? 1 : 0, Array.TrueForAll(a, x => x > 0) ? 1 : 0
  };

  public static int ContainsNaN() => new[] { 1.0, double.NaN }.Contains(double.NaN) ? 1 : 0;

  public static int[] FromList() {
    var list = new List<int> { 4, 1, 3 };
    return list.Where(x => x > 1).OrderBy(x => x).ToArray();
  }

  public static int Words() {
    var words = new[] { "b", "B", "a" };
    return words.OrderBy(w => w, StringComparer.Ordinal).First() == "B" ? 1 : 0;
  }

}
