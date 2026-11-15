// The order of an exception filter with an effect and a finally block inside its try: .NET runs the
// filter first ("W"), then the finally block ("F"), then the handler ("C"). Written for this repository.
#nullable disable
using System;

namespace Polypiler.Fixtures.Prototype.Exceptions;

public static class OrderDemo {

  private static string _log = "";

  private static bool Log(string entry) {
    _log = _log.Length == 0 ? entry : _log + "," + entry;
    return true;
  }

  // "W,F,C".
  public static string Run() {
    _log = "";
    try {
      try {
        throw new InvalidOperationException("x");
      } finally {
        Log("F");
      }
    } catch (InvalidOperationException) when (Log("W")) {
      Log("C");
    }

    return _log;
  }

}
