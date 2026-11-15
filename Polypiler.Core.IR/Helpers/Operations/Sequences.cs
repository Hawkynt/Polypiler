// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The helpers of the sequence operations (#1329): the stable sort the orderings of LINQ and
/// JavaScript's <c>sort</c> take their order from, and the check of the arguments of a range.
/// </summary>
public static partial class HelperOperations {

  /// <summary>
  /// <see cref="IrIntrinsicOp.StableSort"/>: sorts the indices in place so that <paramref name="compare"/>
  /// of neighbours is never positive, keeping the order of those it finds equal. It is the bottom-up
  /// merge sort of Sedgewick and Wayne, <i>Algorithms</i>, 4th ed., §2.2 - runs of 1, 2, 4, … elements
  /// merged through a buffer, the element of the left run taken on a tie - written as code of this
  /// repository from the published algorithm.
  /// </summary>
  /// <param name="items">The indices to sort.</param>
  /// <param name="compare">The order of two indices: negative, zero or positive.</param>
  public static void StableSort(int[] items, Func<int, int, int> compare) {
    var n = items.Length;
    var buffer = new int[n];
    var width = 1;
    while (width < n) {
      var low = 0;
      while (low < n - width) {
        var middle = low + width;
        // high = min(low + 2 * width, n), computed without leaving the int range.
        var high = n - middle > width ? middle + width : n;
        for (var copy = low; copy < high; ++copy)
          buffer[copy] = items[copy];

        var left = low;
        var right = middle;
        for (var index = low; index < high; ++index)
          if (left >= middle)
            items[index] = buffer[right++];
          else if (right >= high)
            items[index] = buffer[left++];
          else if (compare(buffer[right], buffer[left]) < 0)
            items[index] = buffer[right++];
          else
            items[index] = buffer[left++];

        low = high;
      }

      // The next width is twice this one; once that reaches n the array is one run.
      if (width >= n - width)
        break;

      width += width;
    }
  }

  /// <summary>
  /// <see cref="IrIntrinsicOp.RangeCount"/>: the count of a range after the check .NET's
  /// <c>Enumerable.Range</c> makes when it is called - a negative count, or a last number beyond the
  /// largest <c>int</c>, throws an <c>ArgumentOutOfRangeException</c>.
  /// </summary>
  /// <param name="start">The first number.</param>
  /// <param name="count">How many numbers.</param>
  public static int RangeCount(int start, int count) {
    if (count < 0 || (long)start + count - 1 > int.MaxValue)
      throw new ArgumentOutOfRangeException();

    return count;
  }

}
