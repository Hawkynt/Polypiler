// The median of byte values by their histogram, written with LINQ: the histogram counts every value,
// and the lower and the upper median are the first values whose running count passes the middle.
// Written for this repository.
#nullable disable
using System.Linq;

namespace Polypiler.Fixtures.Prototype.Sequences;

public static class ByteMedian {

  public static int[] Histogram(byte[] data) => Enumerable.Range(0, 256).Select(v => data.Count(b => b == v)).ToArray();

  // The mean of the lower and the upper median, rounded down; -1 for no data.
  public static int Median(byte[] data) {
    if (!data.Any())
      return -1;

    var histogram = Histogram(data);
    var lower = Enumerable.Range(0, 256).First(v => histogram.Take(v + 1).Sum() > (data.Length - 1) / 2);
    var upper = Enumerable.Range(0, 256).First(v => histogram.Take(v + 1).Sum() > data.Length / 2);
    return (lower + upper) / 2;
  }

}
