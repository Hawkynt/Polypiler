// SPDX-License-Identifier: MIT
namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerHelpers {

  /// <summary>Whether <c>a + b + carry</c> of two <c>ulong</c> reaches 2^64.</summary>
  public static CSharpHelper CarryOut64 { get; } = new("CarryOut64", "bool", "ulong a, ulong b, bool carry", [
    "unchecked {",
    "  ulong sum = a + b;",
    "  return sum < a || (carry && sum == ulong.MaxValue);",
    "}"
  ]);

  /// <summary>Whether <c>a - b - borrow</c> of two <c>ulong</c> is negative.</summary>
  public static CSharpHelper BorrowOut64 { get; } = new("BorrowOut64", "bool", "ulong a, ulong b, bool borrow", [
    "return a < b || (borrow && a == b);"
  ]);

}
