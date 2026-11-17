using System;

namespace Polypiler.Tests.Ieee.Support;

/// <summary>
/// An oracle of directed rounding that owes nothing to the code under test: the host's own
/// operation, which IEEE 754 rounds to nearest even, gives <c>r</c>; an error-free transformation
/// gives the sign of the exact result less <c>r</c> - Knuth's TwoSum for a sum, the fused multiply-add
/// for a product and for the remainder of a quotient - and whether the exact result lies halfway; the
/// result in the other modes is then <c>r</c> or its neighbour (<see cref="Math.BitIncrement"/>,
/// <see cref="Math.BitDecrement"/>). The transformations are exact while nothing overflows and a
/// product stays clear of the subnormal range, which the callers keep to.
/// </summary>
internal static class IeeeOracle {

  /// <summary>The sum of two doubles in a mode.</summary>
  public static double Add(double left, double right, int mode) {
    var sum = left + right;
    var virtualRight = sum - left;
    var error = (left - (sum - virtualRight)) + (right - virtualRight);
    return Directed(sum, error, mode);
  }

  /// <summary>The product of two doubles in a mode.</summary>
  public static double Multiply(double left, double right, int mode) {
    var product = left * right;
    return Directed(product, Math.FusedMultiplyAdd(left, right, -product), mode);
  }

  /// <summary>The quotient of two doubles in a mode; a quotient of two binary numbers is never a tie.</summary>
  public static double Divide(double left, double right, int mode) {
    var quotient = left / right;
    var remainder = Math.FusedMultiplyAdd(-quotient, right, left);
    var sign = Math.Sign(remainder) * Math.Sign(right);
    return Directed(quotient, sign, mode, tie: false);
  }

  /// <summary>The product of two floats in a mode: it is exact as a double.</summary>
  public static float Multiply(float left, float right, int mode) => Narrowed((double)left * right, mode);

  /// <summary>The sum of two floats whose exact sum a double holds.</summary>
  public static float Add(float left, float right, int mode) => Narrowed((double)left + right, mode);

  /// <summary>The quotient of two floats in a mode, told by the sign of the exact remainder <c>a - q·b</c>.</summary>
  public static float Divide(float left, float right, int mode) {
    var quotient = left / right;
    var remainder = left - (double)quotient * right;
    var sign = Math.Sign(remainder) * Math.Sign(right);
    return sign == 0 ? quotient : Step(quotient, sign, mode);
  }

  /// <summary>An exact double as the float of a mode.</summary>
  private static float Narrowed(double exact, int mode) {
    var nearest = (float)exact;
    var error = exact - nearest;
    if (error == 0)
      return nearest;

    var neighbour = error > 0 ? MathF.BitIncrement(nearest) : MathF.BitDecrement(nearest);
    var tie = Math.Abs(error) * 2 == Math.Abs((double)neighbour - nearest);
    if (mode == 4 && tie)
      return Math.Abs(neighbour) > Math.Abs(nearest) ? neighbour : nearest;

    return Step(nearest, Math.Sign(error), mode);
  }

  private static float Step(float nearest, int sign, int mode) => mode switch {
    1 => (sign > 0) == (nearest < 0) ? (sign > 0 ? MathF.BitIncrement(nearest) : MathF.BitDecrement(nearest)) : nearest,
    2 => sign > 0 ? MathF.BitIncrement(nearest) : nearest,
    3 => sign < 0 ? MathF.BitDecrement(nearest) : nearest,
    _ => nearest
  };

  private static double Directed(double nearest, double error, int mode, bool tie = true) {
    if (error == 0)
      return nearest;

    var up = error > 0;
    var neighbour = up ? Math.BitIncrement(nearest) : Math.BitDecrement(nearest);
    if (mode == 4 && tie && Math.Abs(error) * 2 == Math.Abs(neighbour - nearest))
      return Math.Abs(neighbour) > Math.Abs(nearest) ? neighbour : nearest;

    return mode switch {
      // Towards zero: the neighbour when the exact value lies between it and zero.
      1 => up == (nearest < 0) ? neighbour : nearest,
      2 => up ? neighbour : nearest,
      3 => up ? nearest : neighbour,
      _ => nearest
    };
  }

}
