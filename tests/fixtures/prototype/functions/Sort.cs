// An insertion sort that takes the comparison as a function. Written for this repository.
#nullable disable
using System;

namespace Polypiler.Fixtures.Prototype.Functions;

public static class Sorting {

  private static void InsertionSort(int[] a, Func<int, int, int> compare) {
    for (var i = 1; i < a.Length; i++) {
      var v = a[i];
      var j = i - 1;
      while (j >= 0 && compare(a[j], v) > 0) {
        a[j + 1] = a[j];
        j--;
      }

      a[j + 1] = v;
    }
  }

  public static int[] Ascending(int[] a) {
    InsertionSort(a, (x, y) => x < y ? -1 : x > y ? 1 : 0);
    return a;
  }

  // By the distance to a pivot the caller names, which the comparison captures.
  public static int[] ByDistance(int[] a, int pivot) {
    InsertionSort(a, (x, y) => Math.Abs(x - pivot) - Math.Abs(y - pivot));
    return a;
  }

}
