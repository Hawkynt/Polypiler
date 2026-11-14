// Shapes as a small hierarchy: an interface, an abstract class with an abstract, a virtual and a
// non-virtual method, two sealed classes that override them, calls through the base class and the
// interface, and type tests and casts. Written for this repository.
#nullable disable
namespace Polypiler.Fixtures.Prototype.Dispatch;

public interface IShape {

  double Area();

  int Sides();

}

public abstract class Shape : IShape {

  public abstract double Area();

  public virtual int Sides() => 0;

  public double Twice() => 2 * this.Area();

}

public sealed class Rect : Shape {

  private readonly double _width;

  private readonly double _height;

  public Rect(double width, double height) {
    this._width = width;
    this._height = height;
  }

  public override double Area() => this._width * this._height;

  public override int Sides() => 4;

}

public sealed class Circle : Shape {

  private readonly double _radius;

  public Circle(double radius) {
    this._radius = radius;
  }

  public override double Area() => 3 * this._radius * this._radius;

}

public static class ShapesDemo {

  private static double TwiceOf(Shape shape) => shape.Twice();

  private static int SidesOf(IShape shape) => shape.Sides();

  // 18 for Rect(2, 3) and Circle(1): 2 * (2 * 3) + 2 * (3 * 1 * 1).
  public static double SumTwice(double width, double height, double radius) => TwiceOf(new Rect(width, height)) + TwiceOf(new Circle(radius));

  // 4 for Rect(2, 3) and Circle(1): 4 + 0.
  public static int SumSides(double width, double height, double radius) => SidesOf(new Rect(width, height)) + SidesOf(new Circle(radius));

  public static double AreaThroughInterface(double width, double height) {
    IShape shape = new Rect(width, height);
    return shape.Area();
  }

  // False: a Circle is no Rect.
  public static bool IsRect(double radius) {
    Shape o = new Circle(radius);
    return o is Rect;
  }

  // True: every Shape is an IShape.
  public static bool IsShape(double radius) {
    Shape o = new Circle(radius);
    return o is IShape;
  }

  // True: as yields null for a Circle.
  public static bool AsRectIsNull(double radius) {
    Shape o = new Circle(radius);
    return o as Rect == null;
  }

  // Throws InvalidCastException.
  public static double CastToRect(double radius) {
    Shape o = new Circle(radius);
    return ((Rect)o).Area();
  }

  public static double CastToCircle(double radius) {
    Shape o = new Circle(radius);
    return ((Circle)o).Area();
  }

  // True: null casts to any class and is no object of it.
  public static bool CastOfNull() {
    Shape o = null;
    var rect = (Rect)o;
    return rect == null && !(o is Shape);
  }

  public static double Pattern(double width, double height, bool makeRect) {
    Shape o = new Circle(width);
    if (makeRect)
      o = new Rect(width, height);

    if (o is Rect rect)
      return rect.Area() + 100;

    return o.Area();
  }

}
