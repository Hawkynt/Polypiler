// Showcase: Inheritance, Virtual Dispatch & Interfaces
// Category: Object Model
// Summary: Single inheritance, abstract classes, virtual overrides, interface dispatch, and runtime type checks.
public interface IScalable {
  int Area();
}

public abstract class Shape : IScalable {
  protected int Factor { get; }
  protected Shape(int factor) => this.Factor = factor;
  public abstract int Area();
}

public sealed class Rect : Shape {
  private readonly int _w;
  private readonly int _h;
  public Rect(int w, int h, int factor) : base(factor) {
    this._w = w;
    this._h = h;
  }
  public override int Area() => this._w * this._h * this.Factor;
}

public sealed class Square : Shape {
  private readonly int _side;
  public Square(int side) : base(1) => this._side = side;
  public override int Area() => this._side * this._side;
}

public static class InheritanceShowcase {
  public static int Measure(bool rect, int a, int b) {
    IScalable item;
    if (rect)
      item = new Rect(a, b, 2);
    else
      item = new Square(a);
    int bonus = item is Square ? 1 : 0;
    return item.Area() + bonus;
  }
}
