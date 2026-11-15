// Resource scopes: using statements and declarations over classes that implement IDisposable,
// publicly and explicitly. Every method returns its log - the ids written on construction, 5 by the
// body, minus the ids on disposal - and after "|" the message of the X that left it, if any.
// Written for this repository.
#nullable disable
using System;

namespace Polypiler.Fixtures.Prototype.Resources;

public sealed class X : Exception {

  public X(string message) : base(message) {
  }

}

// Writes its id on construction and minus its id on disposal; built with fail, Dispose throws.
public sealed class Res : IDisposable {

  private readonly int[] _log;
  private readonly int _id;
  private readonly bool _fail;

  public Res(int[] log, int id, bool fail) {
    this._log = log;
    this._id = id;
    this._fail = fail;
    Write(log, id);
  }

  // log[0] counts the entries after it.
  public static void Write(int[] log, int value) {
    log[0] = log[0] + 1;
    log[log[0]] = value;
  }

  public void Dispose() {
    Write(this._log, -this._id);
    if (this._fail)
      throw new X("dispose " + (char)('0' + this._id));
  }

}

// Disposed through the interface only: an explicit implementation.
public sealed class Quiet : IDisposable {

  private readonly int[] _log;
  private readonly int _id;

  public Quiet(int[] log, int id) {
    this._log = log;
    this._id = id;
    Res.Write(log, id);
  }

  void IDisposable.Dispose() {
    Res.Write(this._log, -this._id);
  }

}

public static class ScopesDemo {

  // "1,2,5,-2,-1|": the log and the message, each entry a digit.
  private static string Text(int[] log, string message) {
    var text = "";
    for (var index = 1; index <= log[0]; ++index) {
      if (index > 1)
        text = text + ",";

      var value = log[index];
      if (value < 0) {
        text = text + "-";
        value = -value;
      }

      text = text + (char)('0' + value);
    }

    return text + "|" + message;
  }

  private static void Declarations(int[] log) {
    using var first = new Res(log, 1, false);
    using var second = new Res(log, 2, false);
    Res.Write(log, 5);
  }

  // "1,2,5,-2,-1|": two using declarations, disposed in reverse order at the end of the block.
  public static string Nest() {
    var log = new int[16];
    Declarations(log);
    return Text(log, "");
  }

  // "1,5,-1|body": the body's exception leaves after disposal.
  public static string BodyThrows() {
    var log = new int[16];
    try {
      using (var resource = new Res(log, 1, false)) {
        Res.Write(log, 5);
        throw new X("body");
      }
    } catch (X e) {
      return Text(log, e.Message);
    }
  }

  // "1,5,-1|dispose 1": an exception from Dispose leaves the scope.
  public static string DisposeThrows() {
    var log = new int[16];
    try {
      using (var resource = new Res(log, 1, true)) {
        Res.Write(log, 5);
      }
    } catch (X e) {
      return Text(log, e.Message);
    }

    return Text(log, "");
  }

  // "5|": a null resource is not disposed.
  public static string NullResource() {
    var log = new int[16];
    using (Res resource = null) {
      Res.Write(log, 5);
    }

    return Text(log, "");
  }

  // "1,2,5,-2,-1|dispose 1": both Dispose calls run; the outer one's exception replaces the inner one's.
  public static string BothThrow() {
    var log = new int[16];
    try {
      using (var outer = new Res(log, 1, true))
      using (var inner = new Res(log, 2, true)) {
        Res.Write(log, 5);
      }
    } catch (X e) {
      return Text(log, e.Message);
    }

    return Text(log, "");
  }

  // "1,5,-1|dispose 1": an exception from Dispose replaces the body's.
  public static string BodyAndDisposeThrow() {
    var log = new int[16];
    try {
      using (var resource = new Res(log, 1, true)) {
        Res.Write(log, 5);
        throw new X("body");
      }
    } catch (X e) {
      return Text(log, e.Message);
    }
  }

  // "1,2,5,-2,-1|": several declarators in one statement.
  public static string Declarators() {
    var log = new int[16];
    using (Res first = new Res(log, 1, false), second = new Res(log, 2, false)) {
      Res.Write(log, 5);
    }

    return Text(log, "");
  }

  // "1,5,-1|": a resource given as an expression, held in a variable of its own.
  public static string Expression() {
    var log = new int[16];
    var resource = new Res(log, 1, false);
    using (resource) {
      Res.Write(log, 5);
    }

    return Text(log, "");
  }

  // "1,2,3,5,-3,-2,-1|": three resources.
  public static string Three() {
    var log = new int[16];
    using (var first = new Res(log, 1, false)) {
      using var second = new Res(log, 2, false);
      using var third = new Res(log, 3, false);
      Res.Write(log, 5);
    }

    return Text(log, "");
  }

  private static int Computed(int[] log) {
    using (var resource = new Res(log, 1, false)) {
      return log[0] + 6;
    }
  }

  // "1,-1,7|": the value returned is the one computed before disposal.
  public static string Returns() {
    var log = new int[16];
    Res.Write(log, Computed(log));
    return Text(log, "");
  }

  // "1,5,-1,2,-2|": a break disposes.
  public static string Breaks() {
    var log = new int[16];
    for (var id = 1; id <= 3; ++id) {
      using (var resource = new Res(log, id, false)) {
        if (id == 2)
          break;

        Res.Write(log, 5);
      }
    }

    return Text(log, "");
  }

  // "1,5,-1,2,-2,3,5,-3|": a continue disposes.
  public static string Continues() {
    var log = new int[16];
    for (var id = 1; id <= 3; ++id) {
      using (var resource = new Res(log, id, false)) {
        if (id == 2)
          continue;

        Res.Write(log, 5);
      }
    }

    return Text(log, "");
  }

  // "1,2,5,-2,-1|": an explicit implementation is called, through a variable of the class and of the interface.
  public static string Explicit() {
    var log = new int[16];
    using (var quiet = new Quiet(log, 1)) {
      using (IDisposable disposable = new Quiet(log, 2)) {
        Res.Write(log, 5);
      }
    }

    return Text(log, "");
  }

}
