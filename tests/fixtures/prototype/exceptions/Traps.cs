// Every trap of the IR caught by the clause of its class: a division by zero, an index outside an
// array, a checked overflow, the sign of a NaN, a field read through null and an invalid cast, each
// selecting its own handler of one try. Written for this repository.
#nullable disable
using System;

namespace Polypiler.Fixtures.Prototype.Exceptions;

public sealed class Box {

  public int Value;

}

public class Shape {
}

public sealed class Circle : Shape {
}

public sealed class Square : Shape {

  public int Side;

}

public static class TrapsDemo {

  // k = 0 to 4 raise a trap each; any other k raises none.
  private static int Raise(int k) {
    var zero = k - k;
    var empty = new int[zero];
    Box box = null;
    if (k == 0)
      return 1 / zero;

    if (k == 1)
      return empty[k];

    if (k == 2)
      return checked(int.MaxValue + k);

    if (k == 3)
      return Math.Sign(double.NaN + k);

    if (k == 4)
      return box.Value;

    return k;
  }

  // 1, 2, 3, 4, 5 for k = 0 to 4, and 0 for any other k.
  public static int Classify(int k) {
    try {
      Raise(k);
      return 0;
    } catch (DivideByZeroException) {
      return 1;
    } catch (IndexOutOfRangeException) {
      return 2;
    } catch (OverflowException) {
      return 3;
    } catch (ArithmeticException) {
      return 4;
    } catch (NullReferenceException) {
      return 5;
    }
  }

  // An exception class caught by its base class, by Exception, and not by an unrelated class.
  public static int ByBase(int k) {
    try {
      try {
        Raise(k);
        return 0;
      } catch (IndexOutOfRangeException) {
        return 7;
      }
    } catch (ArithmeticException) {
      return 8;
    } catch (Exception) {
      return 9;
    }
  }

  // 6 for an invalid cast (k = 0), 5 for a field read through null (k = 1), each by its own clause.
  public static int Cast(int k) {
    Shape shape = new Circle();
    Box box = null;
    try {
      if (k == 0)
        return ((Square)shape).Side;

      return box.Value;
    } catch (InvalidCastException) {
      return 6;
    } catch (NullReferenceException) {
      return 5;
    }
  }

}
