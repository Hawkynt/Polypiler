// A counter as an object: a private field, an auto-property with an initializer, computed
// properties, two constructors of which one chains to the other, instance methods and a static
// field that counts the objects made. Written for this repository.
namespace Polypiler.Fixtures.Prototype.Classes;

public sealed class Counter {

  public static int Created;

  private int _count;

  public Counter(int start) {
    this._count = start;
    ++Created;
  }

  public Counter() : this(0) { }

  public int Step { get; set; } = 1;

  public int Count => this._count;

  public bool IsEven {
    get { return (this._count & 1) == 0; }
  }

  public int Next() {
    this._count += this.Step;
    return this._count;
  }

  public void Reset() => this._count = 0;

}

public static class CounterDemo {

  // 16: new Counter(10), Step = 3, Next() twice.
  public static int Run(int start, int step) {
    var counter = new Counter(start);
    counter.Step = step;
    counter.Next();
    return counter.Next();
  }

  public static int CountAfter(int start, int step) {
    var counter = new Counter(start) { Step = step };
    counter.Next();
    counter.Next();
    return counter.Count;
  }

  // 1: b = a shares the object, so b.Next() is seen through a.
  public static int Aliased() {
    var a = new Counter(0);
    var b = a;
    b.Next();
    return a.Count;
  }

  public static bool SameObject() {
    var a = new Counter(0);
    var b = a;
    return ReferenceEquals(a, b) && a == b;
  }

  public static bool TwoObjects() => new Counter(0) == new Counter(0);

  public static bool Even(int start) => new Counter(start).IsEven;

  public static int MadeByBoth() {
    var before = Counter.Created;
    var first = new Counter(5);
    var second = new Counter();
    second.Next();
    first.Reset();
    return Counter.Created - before + first.Count + second.Count;
  }

}
