// Delegates, lambdas, local functions and closures. Measured with .NET 10: ForCapture() 9,
// ForeachCapture([0, 1, 2]) 3, Counter() 123, Interleaved() 21, Late(2) 10, Fact(5) 120, Fact(0) 1,
// Fact(13) 1932053504, Composed(4) 15, MethodGroup(-7) 49; CallNull() throws NullReferenceException.
// Written for this repository.
#nullable disable
using System;

namespace Polypiler.Fixtures.Prototype.Functions;

/// <summary>A delegate type of the file, which the IR takes as the function type of its signature.</summary>
public delegate int Op(int a, int b);

public sealed class Scale {

  private readonly int _factor;

  public Scale(int factor) {
    this._factor = factor;
  }

  public int Apply(int v) => v * this._factor;

}

public static class Closures {

  // A C# for shares its variable: every closure sees the 3 it ends with.
  public static int ForCapture() {
    var fs = new Func<int>[3];
    var k = 0;
    for (var i = 0; i < 3; i++)
      fs[k++] = () => i;

    return fs[0]() + fs[1]() + fs[2]();
  }

  // A foreach makes a variable per pass (C# 5).
  public static int ForeachCapture(int[] values) {
    var fs = new Func<int>[values.Length];
    var k = 0;
    foreach (var v in values)
      fs[k++] = () => v;

    var s = 0;
    foreach (var f in fs)
      s += f();

    return s;
  }

  private static Func<int> MakeCounter() {
    var n = 0;
    return () => ++n;
  }

  public static int Counter() {
    var a = MakeCounter();
    return a() * 100 + a() * 10 + a();
  }

  public static int Interleaved() {
    var a = MakeCounter();
    a();
    var b = MakeCounter();
    return a() * 10 + b();
  }

  // The closure sees the write made after it.
  public static int Late(int x) {
    Func<int> g = () => x;
    x = x * 5;
    return g();
  }

  public static int Fact(int n) {
    return F(n);

    int F(int m) => m <= 1 ? 1 : m * F(m - 1);
  }

  private static Func<int, int> Compose(Func<int, int> f, Func<int, int> g) => v => g(f(v));

  public static int Composed(int x) => Compose(v => v + 1, v => v * 3)(x);

  private static int Square(int v) => v * v;

  public static int MethodGroup(int x) {
    Func<int, int> f = Square;
    return f(x);
  }

  public static int CallNull() {
    Func<int> f = null;
    return f();
  }

  public static int Anonymous(int x) {
    Func<int, int> twice = delegate (int v) { return v * 2; };
    return twice(x) + 1;
  }

  public static int StaticLambda(int a, int b) {
    Op add = static (p, q) => p + q;
    return add.Invoke(a, b);
  }

  // The receiver is evaluated when the delegate is made, not when it is called.
  public static int Bound(int v) {
    var scale = new Scale(3);
    Func<int, int> f = scale.Apply;
    scale = null;
    return f(v);
  }

  public static bool NullChecks() {
    Func<int> f = null;
    var wasNull = f == null;
    f = () => 1;
    return wasNull && f != null && !(f is null);
  }

  // Two closures share one variable.
  public static int Shared(int times) {
    var c = 0;
    Action step = () => c++;
    Func<int> read = () => c;
    for (var i = 0; i < times; i++)
      step();

    return read();
  }

  public static int StaticLocal(int x) {
    return Twice(x) + Twice(1);

    static int Twice(int v) => v * 2;
  }

  // A closure made by a closure captures through both.
  public static int Deep(int x) {
    Func<Func<int>> outer = () => () => x + 1;
    return outer()();
  }

  // A local of a loop body is a new variable every pass.
  public static int BodyLocal() {
    var fs = new Func<int>[3];
    var t = 0;
    while (t < 3) {
      var copy = t * 10;
      fs[t] = () => copy;
      t++;
    }

    return fs[0]() + fs[1]() + fs[2]();
  }

}
