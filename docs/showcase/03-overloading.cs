// Showcase: Method & Constructor Overloading
// Category: Object Model
// Summary: Overloaded constructors and overloaded instance methods resolved by arity and type.
public sealed class Accumulator {
  private int _total;

  public Accumulator() => this._total = 0;
  public Accumulator(int seed) => this._total = seed;

  public int Add(int value) {
    this._total += value;
    return this._total;
  }

  public int Add(int a, int b) {
    this._total += a + b;
    return this._total;
  }
}

public static class OverloadingShowcase {
  public static int Run(int seed, int x, int y) {
    var a = new Accumulator();
    var b = new Accumulator(seed);
    return a.Add(x) + b.Add(x, y);
  }
}
