// Exception regions with finally blocks: a try with catch clauses and a finally (the shape of
// async/Pipeline's Observed, without the awaits), a return, a break and a continue through a finally,
// every way out of one try, two finally blocks between a throw and a filtered handler (.NET runs the
// filter first), a filter that declines, a clause that does not take the exception, a finally around
// a call that throws and a throw inside a finally. Written for this repository.
#nullable disable
using System;

namespace Polypiler.Fixtures.Prototype.Exceptions;

public sealed class RegionException : Exception {

  public RegionException(string message) : base(message) {
  }

}

public static class RegionsDemo {

  private static string _log = "";

  private static bool Log(string entry) {
    _log += entry;
    return true;
  }

  private static string Thrower(bool fail) {
    if (fail)
      throw new RegionException("boom");

    return "ok";
  }

  // "t1 caught boom finally" and "t1 ok finally".
  public static string Observed(bool fail) {
    var trace = "t1";
    try {
      trace += " " + Thrower(fail);
    } catch (RegionException e) {
      trace += " caught " + e.Message;
    } finally {
      trace += " finally";
    }

    return trace;
  }

  // The value of the return is read before the finally runs: 2 * k for k > 0, else 99.
  public static int Returned(int k) {
    var s = k;
    try {
      if (k > 0)
        return s * 2;

      s = -1;
    } finally {
      s += 100;
    }

    return s;
  }

  // A break and a continue through a finally, which runs on every pass.
  public static int Jumps(int n) {
    var s = 0;
    for (var i = 0; i < n; ++i) {
      try {
        if (i == 5)
          break;
        if (i % 2 == 0)
          continue;

        s += i;
      } finally {
        s += 100;
      }
    }

    return s;
  }

  // Every way out of one try: 0 a return, 1 an exception, 2 a break, 3 a continue, else none.
  public static string Ways(int k) {
    var trace = "";
    for (var i = 0; i < 2; ++i) {
      try {
        try {
          if (k == 0)
            return trace + "r";
          if (k == 1)
            throw new RegionException("e");
          if (k == 2)
            break;
          if (k == 3)
            continue;

          trace += "n";
        } finally {
          trace += "f";
        }
      } catch (RegionException e) {
        trace += e.Message;
      }

      trace += ".";
    }

    return trace;
  }

  // Two finally blocks between the throw and a filtered handler: "WABC".
  public static string Nested() {
    _log = "";
    try {
      try {
        try {
          throw new RegionException("x");
        } finally {
          Log("A");
        }
      } finally {
        Log("B");
      }
    } catch (RegionException) when (Log("W")) {
      Log("C");
    }

    return _log;
  }

  // A filter that declines: the next clause takes the exception after the finally ran, "WFD"; else "WFC".
  public static string Declined(bool take) {
    _log = "";
    try {
      try {
        throw new RegionException("x");
      } finally {
        Log("F");
      }
    } catch (RegionException) when (Log("W") && take) {
      Log("C");
    } catch (Exception) {
      Log("D");
    }

    return _log;
  }

  // A clause that does not take the exception: the finally runs, the outer handler takes it, "fp"; else "nf".
  public static string Passed(int k) {
    var trace = "";
    try {
      try {
        if (k > 0)
          throw new RegionException("p");

        trace += "n";
      } catch (InvalidOperationException) {
        trace += "i";
      } finally {
        trace += "f";
      }
    } catch (RegionException e) {
      trace += e.Message;
    }

    return trace;
  }

  private static int Check(int k) {
    if (k < 0)
      throw new RegionException("neg");

    return k;
  }

  // A finally around a call that throws: "pf", "zf", "fneg".
  public static string AroundCall(int k) {
    var trace = "";
    try {
      try {
        trace += Check(k) > 0 ? "p" : "z";
      } finally {
        trace += "f";
      }
    } catch (RegionException e) {
      trace += e.Message;
    }

    return trace;
  }

  // An exception thrown in a finally replaces the one leaving: "second".
  public static string Replaced() {
    try {
      try {
        throw new RegionException("first");
      } finally {
        throw new RegionException("second");
      }
    } catch (RegionException e) {
      return e.Message;
    }
  }

}
