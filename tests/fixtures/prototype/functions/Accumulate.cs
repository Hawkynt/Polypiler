// A fold over an array with a callback. Written for this repository.
#nullable disable
using System;

namespace Polypiler.Fixtures.Prototype.Functions;

public static class Accumulate {

  private static double Fold(double[] a, double seed, Func<double, double, double> step) {
    var result = seed;
    foreach (var v in a)
      result = step(result, v);

    return result;
  }

  public static double Sum(double[] a) => Fold(a, 0, (s, v) => s + v);

  public static double SumOfSquares(double[] a) => Fold(a, 0, (s, v) => s + v * v);

  // The callback counts the calls in a variable of the caller.
  public static int Calls(double[] a) {
    var calls = 0;
    Fold(a, 1, (s, v) => {
      calls++;
      return s * v;
    });
    return calls;
  }

}
