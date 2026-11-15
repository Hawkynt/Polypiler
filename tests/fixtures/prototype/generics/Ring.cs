// A ring buffer of a fixed capacity as a generic class, used over long and over double: the last
// value pushed and the number of values pushed. Written for this repository.
namespace Polypiler.Fixtures.Prototype.Generics;

public sealed class Ring<T> {

  private readonly T[] _items;

  private int _count;

  public Ring(int capacity) {
    this._items = new T[capacity];
  }

  public void Push(T value) {
    this._items[this._count % this._items.Length] = value;
    ++this._count;
  }

  public T Last => this._items[(this._count - 1) % this._items.Length];

  public int Count => this._count;

}

public static class RingDemo {

  private static Ring<long> Longs() {
    var ring = new Ring<long>(3);
    ring.Push(1);
    ring.Push(2);
    ring.Push(3);
    ring.Push(9007199254740993);
    return ring;
  }

  // 9007199254740993, which a double does not hold.
  public static long LastLong() => Longs().Last;

  // 4.
  public static int CountLong() => Longs().Count;

  // -Infinity: the ring keeps the sign of -0.0.
  public static double ReciprocalOfLastDouble() {
    var ring = new Ring<double>(2);
    ring.Push(0.5);
    ring.Push(-0.0);
    return 1.0 / ring.Last;
  }

}
