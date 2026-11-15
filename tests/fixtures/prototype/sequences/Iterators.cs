// Iterator methods of C#: laziness, restarting, finally on every way out of a foreach, an exception
// after the first value, nested iterators and an infinite sequence with int32 wrap-around. Every
// function records the order of events in t[0] = t[0] * 10 + code: 5 the sequence is made, 7 the
// body starts, 1 to 3 a value is produced, 8 the consumer's body runs, 9 a finally block runs, 6 the
// body ends normally, 4 an exception is caught. Written for this repository.
#nullable disable
using System;
using System.Collections.Generic;

namespace Polypiler.Fixtures.Prototype.Sequences;

public sealed class Counter {

  public int Start;

  public Counter(int start) {
    this.Start = start;
  }

  public IEnumerable<int> Upwards(int count) {
    for (var i = 0; i < count; ++i)
      yield return this.Start + i;
  }

}

public static class Iterators {

  private static void Step(int[] t, int code) {
    t[0] = t[0] * 10 + code;
  }

  private static IEnumerable<int> Traced(int[] t) {
    Step(t, 7);
    try {
      Step(t, 1);
      yield return 1;
      Step(t, 2);
      yield return 2;
      Step(t, 3);
      yield return 3;
    } finally {
      Step(t, 9);
    }

    Step(t, 6);
  }

  private static IEnumerable<int> Up(int n) {
    for (var i = 1; i <= n; ++i)
      yield return i;
  }

  private static IEnumerable<int> Fib() {
    var a = 0;
    var b = 1;
    while (true) {
      yield return a;
      var c = a + b;
      a = b;
      b = c;
    }
  }

  private static IEnumerable<int> Gate(int n) {
    if (n < 0)
      yield break;

    yield return n + 1;
  }

  private static IEnumerable<int> Thrower(int[] t) {
    Step(t, 1);
    yield return 1;
    throw new InvalidOperationException("after the first value");
  }

  private static IEnumerable<int> Doubled(int n) {
    foreach (var v in Up(n))
      yield return v;

    foreach (var v in Up(n))
      yield return v * v * 2;
  }

  private static IEnumerable<int> Down(int n) {
    while (n > 0) {
      yield return n;
      --n;
    }
  }

  private static IEnumerable<T> Take<T>(IEnumerable<T> source, int count) {
    if (count <= 0)
      yield break;

    var taken = 0;
    foreach (var item in source) {
      yield return item;
      if (++taken == count)
        yield break;
    }
  }

  private static IEnumerable<long> Wide() {
    yield return 1L << 40;
    yield return -3;
  }

  private static IEnumerable<double> Halves() {
    yield return 0.5;
    yield return -2.25;
  }

  private static IEnumerable<bool> Flags() {
    yield return true;
    yield return false;
    yield return true;
  }

  private static IEnumerable<Counter> Counters() {
    yield return new Counter(3);
    yield return new Counter(4);
  }

  private static IEnumerable<int> Inner(int[] t) {
    try {
      yield return 1;
      yield return 2;
    } finally {
      Step(t, 8);
    }
  }

  private static IEnumerable<int> Outer(int[] t) {
    try {
      foreach (var v in Inner(t))
        yield return v;

      Step(t, 3);
      yield return 3;
    } finally {
      Step(t, 9);
    }
  }

  private static IEnumerable<int> Listed(List<int> list) {
    foreach (var x in list)
      yield return x * 2;
  }

  private static IEnumerable<int> Entries(Dictionary<int, int> map) {
    foreach (var entry in map)
      yield return entry.Key * 10 + entry.Value;
  }

  private static IEnumerable<Func<int>> Makers(int n) {
    for (var i = 0; i < n; ++i) {
      var k = i;
      yield return () => k * 10;
    }
  }

  private static int Sum(IEnumerable<int> values) {
    var sum = 0;
    foreach (var v in values)
      sum += v;

    return sum;
  }

  private static int ReturnInside(int[] t) {
    foreach (var v in Traced(t)) {
      if (v == 2)
        return v;
    }

    return 0;
  }

  public static int EarlyBreak() {
    var t = new int[1];
    var s = Traced(t);
    Step(t, 5);
    foreach (var v in s) {
      Step(t, 8);
      break;
    }

    return t[0];
  }

  public static int Full() {
    var t = new int[1];
    foreach (var v in Traced(t))
      Step(t, 8);

    return t[0];
  }

  public static int Twice() {
    var s = Up(2);
    return Sum(s) + Sum(s);
  }

  public static int FibSum(int n) {
    var sum = 0;
    var i = 0;
    foreach (var v in Fib()) {
      if (i == n)
        break;

      sum += v;
      ++i;
    }

    return sum;
  }

  public static int Guard(int n) {
    var count = 0;
    foreach (var v in Gate(n))
      ++count;

    return count;
  }

  public static int Throws() {
    var t = new int[1];
    try {
      foreach (var v in Thrower(t)) {
      }
    } catch (InvalidOperationException) {
      Step(t, 4);
    }

    return t[0];
  }

  public static int Nested(int n) => Sum(Doubled(n));

  public static int Returned() {
    var t = new int[1];
    var r = ReturnInside(t);
    Step(t, r);
    return t[0];
  }

  public static int ThrowInside() {
    var t = new int[1];
    try {
      foreach (var v in Traced(t))
        throw new InvalidOperationException("in the body");
    } catch (InvalidOperationException) {
      Step(t, 4);
    }

    return t[0];
  }

  public static int NestedClose() {
    var t = new int[1];
    foreach (var v in Outer(t)) {
      Step(t, v);
      break;
    }

    return t[0];
  }

  public static int NestedFull() {
    var t = new int[1];
    foreach (var v in Outer(t))
      Step(t, v);

    return t[0];
  }

  public static int Restarted() {
    var t = new int[1];
    var s = Down(2);
    foreach (var v in s)
      Step(t, v);

    foreach (var v in s)
      Step(t, v);

    return t[0];
  }

  public static int Taken(int count) => Sum(Take(Fib(), count));

  public static int FromArray() {
    var digits = new[] { 4, 5, 6 };
    return Sum(digits) + Sum(Take(digits, 2)) * 100;
  }

  public static long Longs() {
    long sum = 0;
    foreach (var v in Wide())
      sum += v;

    return sum;
  }

  public static int Floats() {
    var product = 1.0;
    foreach (var v in Halves())
      product *= v;

    return (int)(product * -100);
  }

  public static int Truths() {
    var t = new int[1];
    foreach (var b in Flags())
      Step(t, b ? 1 : 2);

    return t[0];
  }

  public static int Objects() {
    var t = new int[1];
    foreach (var c in Counters())
      Step(t, c.Start);

    return t[0];
  }

  public static int Method(int start) {
    var counter = new Counter(start);
    var sum = 0;
    foreach (var v in counter.Upwards(3))
      sum = sum * 10 + v;

    return sum;
  }

  public static int FromList() {
    var list = new List<int> { 1, 2, 3 };
    var map = new Dictionary<int, int> { { 1, 2 }, { 3, 4 } };
    return Sum(Listed(list)) * 1000 + Sum(Entries(map));
  }

  public static int Captured() {
    var all = new Func<int>[3];
    var at = 0;
    foreach (var f in Makers(3)) {
      all[at] = f;
      ++at;
    }

    var sum = 0;
    foreach (var g in all)
      sum += g();

    return sum;
  }

  public static long Widened() {
    long sum = 0;
    foreach (long v in Up(3))
      sum = sum * 10 + v;

    return sum;
  }

}
