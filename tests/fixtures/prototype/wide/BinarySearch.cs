// Binary search in a sorted array, written with the jumps a hand-written loop uses: continue to go on
// with a half, break when the key is found. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class BinarySearch {

  public static int Find(int[] sorted, int key) {
    int low = 0;
    int high = sorted.Length - 1;
    int found = -1;
    while (low <= high) {
      int middle = low + ((high - low) >> 1);
      if (sorted[middle] < key) {
        low = middle + 1;
        continue;
      }

      if (sorted[middle] > key) {
        high = middle - 1;
        continue;
      }

      found = middle;
      break;
    }

    return found;
  }

  // The index of the first element that is not smaller than the key; the length when there is none.
  public static int LowerBound(long[] sorted, long key) {
    int low = 0;
    int high = sorted.Length;
    for (;;) {
      if (low >= high)
        break;

      int middle = (int)(((uint)low + (uint)high) >> 1);
      if (sorted[middle] < key)
        low = middle + 1;
      else
        high = middle;
    }

    return low;
  }

  public static int FindDouble(double[] sorted, double key) {
    int low = 0;
    int high = sorted.Length - 1;
    while (low <= high) {
      int middle = (low + high) >>> 1;
      if (sorted[middle] == key)
        return middle;

      if (sorted[middle] < key)
        low = middle + 1;
      else
        high = middle - 1;
    }

    return ~low;
  }

}
