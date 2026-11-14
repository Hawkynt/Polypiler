// SPDX-License-Identifier: MIT
namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerHelpers {

  /// <summary>Whether an exact result of up to 33 bits, or 64 for a product, lies outside two bounds.</summary>
  public static CSharpHelper Outside { get; } = new("Outside", "bool", "long value, long low, long high", [
    "return value < low || value > high;"
  ]);

  /// <summary>Whether the sum of two <c>long</c> overflows: both operands differ in sign from the wrapped sum.</summary>
  public static CSharpHelper AddOverflowsSigned64 { get; } = new("AddOverflowsSigned64", "bool", "long a, long b", [
    "unchecked {",
    "  long sum = a + b;",
    "  return ((a ^ sum) & (b ^ sum)) < 0L;",
    "}"
  ]);

  /// <summary>Whether the difference of two <c>long</c> overflows.</summary>
  public static CSharpHelper SubOverflowsSigned64 { get; } = new("SubOverflowsSigned64", "bool", "long a, long b", [
    "unchecked {",
    "  long difference = a - b;",
    "  return ((a ^ b) & (a ^ difference)) < 0L;",
    "}"
  ]);

  /// <summary>Whether the product of two <c>long</c> overflows.</summary>
  public static CSharpHelper MulOverflowsSigned64 { get; } = new("MulOverflowsSigned64", "bool", "long a, long b", [
    "unchecked {",
    "  if (a == 0L || b == 0L) {",
    "    return false;",
    "  }",
    "  return (a == -1L && b == long.MinValue) || (b == -1L && a == long.MinValue) || (a * b) / b != a;",
    "}"
  ]);

  /// <summary>Whether the sum of two <c>ulong</c> wraps around.</summary>
  public static CSharpHelper AddOverflowsUnsigned64 { get; } = new("AddOverflowsUnsigned64", "bool", "ulong a, ulong b", [
    "return unchecked(a + b) < a;"
  ]);

  /// <summary>Whether the product of two <c>ulong</c> leaves the 64 bits.</summary>
  public static CSharpHelper MulOverflowsUnsigned64 { get; } = new("MulOverflowsUnsigned64", "bool", "ulong a, ulong b", [
    "return a != 0UL && b > ulong.MaxValue / a;"
  ]);

}
