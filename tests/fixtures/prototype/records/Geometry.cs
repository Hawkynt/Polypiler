// A vector of two doubles as a struct, and the places where C# copies one: an assignment, an
// argument, a returned value, a store into an array and a load from one. Copy and ArrayCopy observe
// each copy in their result; in JavaScript without the copies they would see the aliasing instead.
// Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class Geometry {

  public struct Vec2 {

    public double X;
    public double Y;

    public Vec2(double x, double y) {
      X = x;
      Y = y;
    }

  }

  public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

  // Writes the copy it is given, which the caller's value never sees.
  public static Vec2 Translate(Vec2 v, double dx) {
    v.X += dx;
    return v;
  }

  // p.X * 100 + q.X * 10 + r.X: 1, 5 and 11, so 161.
  public static double Copy() {
    var p = new Vec2(1, 2);
    var q = p;
    q.X = 5;
    var r = Translate(p, 10);
    return p.X * 100 + q.X * 10 + r.X;
  }

  // a[0].X * 100 + a[1].X * 10 + p.X: 3, 9 and 7, so 397.
  public static double ArrayCopy() {
    var a = new Vec2[2];
    a[0].X = 3;
    var p = a[0];
    p.X = 9;
    a[1] = p;
    p.X = 7;
    return a[0].X * 100 + a[1].X * 10 + p.X;
  }

  public static double DotOf(double ax, double ay, double bx, double by) => Dot(new Vec2(ax, ay), new Vec2(bx, by));

  // The sum of the elements of an array of n vectors (i, 2i), each read through a copy.
  public static double Sum(int n) {
    var points = new Vec2[n];
    for (var i = 0; i < n; ++i)
      points[i] = new Vec2(i, 2 * i);

    var total = new Vec2();
    foreach (var point in points) {
      total.X += point.X;
      total.Y += point.Y;
    }

    return total.X + total.Y;
  }

  // The element at an index of an array of two, which traps outside it.
  public static double At(int index) {
    var points = new[] { new Vec2(1, 2), new Vec2(3, 4) };
    return points[index].Y;
  }

}
