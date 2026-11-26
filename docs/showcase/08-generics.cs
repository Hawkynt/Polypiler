// Showcase: Generics
// Category: Object Model
// Summary: Constrained generic static methods and generic classes monomorphized per type instantiation.
using System;

public sealed class Box<T> where T : struct {
  public T Value { get; set; }
  public Box(T initial) => this.Value = initial;
}

public static class GenericsShowcase {
  private static T Clamp<T>(T value, T min, T max) where T : IComparable<T> {
    if (value.CompareTo(min) < 0)
      return min;
    if (value.CompareTo(max) > 0)
      return max;
    return value;
  }

  public static long Run(int a, long b) {
    var intBox = new Box<int>(Clamp(a, 0, 100));
    var longBox = new Box<long>(Clamp(b, -50L, 50L));
    return intBox.Value + longBox.Value;
  }
}
