// Bubble sort on an array of short, in place. The outer loop ends early when a pass swapped nothing.
// Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class BubbleSort {

  public static void Sort(short[] data) {
    for (int end = data.Length - 1; end > 0; --end) {
      bool swapped = false;
      for (int i = 0; i < end; ++i) {
        if (data[i] <= data[i + 1])
          continue;

        short held = data[i];
        data[i] = data[i + 1];
        data[i + 1] = held;
        swapped = true;
      }

      if (!swapped)
        break;
    }
  }

  // The number of swaps a bubble sort of the data makes, which is its number of inversions.
  public static int Swaps(short[] data) {
    short[] copy = new short[data.Length];
    int at = 0;
    foreach (short value in data)
      copy[at++] = value;

    int swaps = 0;
    for (int end = copy.Length - 1; end > 0; --end)
      for (int i = 0; i < end; ++i)
        if (copy[i] > copy[i + 1]) {
          short held = copy[i];
          copy[i] = copy[i + 1];
          copy[i + 1] = held;
          ++swaps;
        }

    return swaps;
  }

  public static bool IsSorted(short[] data) {
    for (int i = 1; i < data.Length; ++i)
      if (data[i - 1] > data[i])
        return false;

    return true;
  }

}
