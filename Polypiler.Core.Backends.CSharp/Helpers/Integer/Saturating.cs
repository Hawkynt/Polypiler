namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerHelpers {

  /// <summary>An exact result of up to 33 bits, or 64 for a product, as the nearest value between two bounds.</summary>
  public static CSharpHelper Bounded { get; } = new("Bounded", "long", "long value, long low, long high", [
    "return value < low ? low : value > high ? high : value;"
  ]);

  /// <summary>An exact unsigned sum or product as the nearest value not above a bound.</summary>
  public static CSharpHelper BoundedAbove { get; } = new("BoundedAbove", "ulong", "ulong value, ulong high", [
    "return value > high ? high : value;"
  ]);

  /// <summary>The sum of two <c>long</c>, the nearest bound where it overflows: when both operands differ in sign from the sum.</summary>
  public static CSharpHelper AddSaturateSigned64 { get; } = new("AddSaturateSigned64", "long", "long a, long b", [
    "unchecked {",
    "  long sum = a + b;",
    "  return ((a ^ sum) & (b ^ sum)) < 0L ? (a < 0L ? long.MinValue : long.MaxValue) : sum;",
    "}"
  ]);

  /// <summary>The difference of two <c>long</c>, the nearest bound where it overflows: when the operands differ in sign and the difference differs from the minuend.</summary>
  public static CSharpHelper SubSaturateSigned64 { get; } = new("SubSaturateSigned64", "long", "long a, long b", [
    "unchecked {",
    "  long difference = a - b;",
    "  return ((a ^ b) & (a ^ difference)) < 0L ? (a < 0L ? long.MinValue : long.MaxValue) : difference;",
    "}"
  ]);

  /// <summary>
  /// The product of two <c>long</c>, the bound of the sign of the exact product where it overflows:
  /// where the wrapped product divided by one factor is not the other, or the smallest value meets -1.
  /// </summary>
  public static CSharpHelper MulSaturateSigned64 { get; } = new("MulSaturateSigned64", "long", "long a, long b", [
    "unchecked {",
    "  if (a == 0L || b == 0L) {",
    "    return 0L;",
    "  }",
    "  long product = a * b;",
    "  if ((a == -1L && b == long.MinValue) || (b == -1L && a == long.MinValue) || product / b != a) {",
    "    return (a < 0L) == (b < 0L) ? long.MaxValue : long.MinValue;",
    "  }",
    "  return product;",
    "}"
  ]);

  /// <summary>The sum of two <c>ulong</c>, all ones where it wraps around.</summary>
  public static CSharpHelper AddSaturateUnsigned64 { get; } = new("AddSaturateUnsigned64", "ulong", "ulong a, ulong b", [
    "unchecked {",
    "  ulong sum = a + b;",
    "  return sum < a ? ulong.MaxValue : sum;",
    "}"
  ]);

  /// <summary>The difference of two <c>ulong</c>, 0 where it would be negative.</summary>
  public static CSharpHelper SubSaturateUnsigned64 { get; } = new("SubSaturateUnsigned64", "ulong", "ulong a, ulong b", [
    "return a < b ? 0UL : a - b;"
  ]);

  /// <summary>The product of two <c>ulong</c>, all ones where it leaves the 64 bits.</summary>
  public static CSharpHelper MulSaturateUnsigned64 { get; } = new("MulSaturateUnsigned64", "ulong", "ulong a, ulong b", [
    "if (a != 0UL && b > ulong.MaxValue / a) {",
    "  return ulong.MaxValue;",
    "}",
    "return a * b;"
  ]);

}
