// Generic helpers: IndexOf over IEquatable<T>, Swap, Max over IComparable<T>, Def and Make, and
// the methods that instantiate them - IndexOf, Swap and Max over int and double, Def over int,
// double, bool and a class, Make over a class. Written for this repository.
#nullable disable
using System;

namespace Polypiler.Fixtures.Prototype.Generics;

public sealed class Box {

  public int Value;

}

public static class SearchDemo {

  public static int IndexOf<T>(T[] a, T v) where T : IEquatable<T> {
    for (var i = 0; i < a.Length; ++i)
      if (a[i].Equals(v))
        return i;

    return -1;
  }

  public static void Swap<T>(T[] a, int i, int j) {
    var t = a[i];
    a[i] = a[j];
    a[j] = t;
  }

  public static T Max<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) >= 0 ? a : b;

  public static T Def<T>() => default(T);

  public static T Make<T>() where T : new() => new T();

  // 1: Double.Equals finds a NaN, where == does not.
  public static int IndexOfNaN() => IndexOf(new[] { 1.0, double.NaN }, double.NaN);

  // 0: 0.0 equals -0.0.
  public static int IndexOfZero() => IndexOf(new[] { -0.0 }, 0.0);

  // 2.
  public static int IndexOfSeven() => IndexOf(new[] { 3, 5, 7 }, 7);

  // -1.
  public static int IndexOfFour() => IndexOf(new[] { 3, 5, 7 }, 4);

  public static int IndexOfDouble(double[] a, double v) => IndexOf(a, v);

  public static int IndexOfInt(int[] a, int v) => IndexOf(a, v);

  // 21.
  public static int SwapInts() {
    var a = new[] { 1, 2 };
    Swap(a, 0, 1);
    return a[0] * 10 + a[1];
  }

  // 2.
  public static double SwapDoubles() {
    var a = new[] { 0.5, 2.5 };
    Swap(a, 0, 1);
    return a[0] - a[1];
  }

  // -Infinity: a NaN is below every number.
  public static double MaxNaN() => Max(double.NaN, double.NegativeInfinity);

  // -Infinity: -0.0 and 0.0 are equal, so the first is the maximum.
  public static double MaxZero() => 1.0 / Max(-0.0, 0.0);

  public static int MaxInt(int a, int b) => Max(a, b);

  public static double MaxDouble(double a, double b) => Max(a, b);

  public static int DefInt() => Def<int>();

  public static double DefDouble() => Def<double>();

  public static bool DefBool() => Def<bool>();

  public static bool DefBoxIsNull() => Def<Box>() == null;

  public static bool MakeBoxIsNew() => !ReferenceEquals(Make<Box>(), Make<Box>());

  public static int MakeBoxValue() => Make<Box>().Value;

}
