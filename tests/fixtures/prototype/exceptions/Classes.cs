// Exception classes of the file: one under Exception with the three constructors of .NET, one two
// levels deep, one under a library class; an inner exception, a filter, and a finally block that
// runs after the value of a return is computed. Written for this repository.
#nullable disable
using System;

namespace Polypiler.Fixtures.Prototype.Exceptions;

public class X : Exception {

  public X() {
  }

  public X(string message) : base(message) {
  }

  public X(string message, Exception inner) : base(message, inner) {
  }

}

public sealed class Y : X {

  public Y(string message) : base(message) {
  }

}

public sealed class Z : InvalidOperationException {

  public Z(string message) : base(message) {
  }

}

public static class ClassesDemo {

  private static int _state;

  // "outer/cause": the message of the exception and of its inner exception.
  public static string Chain() {
    try {
      try {
        throw new X("cause");
      } catch (X inner) {
        throw new X("outer", inner);
      }
    } catch (X outer) {
      return outer.Message + "/" + outer.InnerException.Message;
    }
  }

  // 1 for "x", which the filter selects, and 2 for any other message.
  public static int Filter(string message) {
    try {
      throw new X(message);
    } catch (X e) when (e.Message == "x") {
      return 1;
    } catch (X) {
      return 2;
    }
  }

  // 1, the value computed before the finally block sets the field to 2.
  public static int Fin() {
    _state = 0;
    try {
      _state = 1;
      return _state;
    } finally {
      _state = 2;
    }
  }

  // 2: the field after Fin().
  public static int FinState() {
    Fin();
    return _state;
  }

  // The message of an exception made without one.
  public static string DefaultMessage() => new X().Message;

  // 3: a class two levels deep is caught by each ancestor, the nearest first.
  public static int Deep(int k) {
    try {
      if (k == 0)
        throw new Y("y");

      if (k == 1)
        throw new X("x");

      throw new Z("z");
    } catch (Y) {
      return 1;
    } catch (X) {
      return 2;
    } catch (InvalidOperationException e) {
      return e.Message.Length;
    }
  }

  // "z": a rethrow keeps the exception, which an outer clause catches.
  public static string Rethrow() {
    try {
      try {
        throw new Z("z");
      } catch (Exception) {
        _state = 5;
        throw;
      }
    } catch (Z e) {
      return e.Message;
    }
  }

  // 7: a throw expression in a coalescing and in a conditional.
  public static int Thrown(int k) {
    var text = k > 0 ? "seven" : throw new X("none");
    try {
      return text.Length + (k > 1 ? 2 : throw new Y("one"));
    } catch (Y) {
      return 0;
    }
  }

}
