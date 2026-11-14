// A histogram of four buckets: the elements of an array in a static readonly field change, the
// array does not. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class Histogram {

  private static readonly uint[] _buckets = new uint[4];

  public static uint Add(int value) {
    var bucket = value & 3;
    ++_buckets[bucket];
    return _buckets[bucket];
  }

}
