namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerHelpers {

  /// <summary>
  /// The upper half of the product of two <c>ulong</c> in four partial products of 32 bits (Warren,
  /// <i>Hacker's Delight</i>, 2nd ed., §8-2): no partial sum leaves the 64 bits.
  /// </summary>
  public static CSharpHelper MulHighUnsigned64 { get; } = new("MulHighUnsigned64", "ulong", "ulong left, ulong right", [
    "unchecked {",
    .. HighProduct,
    "  return high;",
    "}"
  ]);

  /// <summary>
  /// The upper half of the product of two <c>long</c>: that of their patterns, less the other factor
  /// for each negative one (§8-3), which is the product of the signed numbers modulo 2^64.
  /// </summary>
  public static CSharpHelper MulHighSigned64 { get; } = new("MulHighSigned64", "long", "long a, long b", [
    "unchecked {",
    "  ulong left = (ulong)a;",
    "  ulong right = (ulong)b;",
    .. HighProduct,
    "  if (a < 0L) {",
    "    high -= right;",
    "  }",
    "  if (b < 0L) {",
    "    high -= left;",
    "  }",
    "  return (long)high;",
    "}"
  ]);

  /// <summary>The statements that leave the upper half of <c>left * right</c> in <c>high</c>.</summary>
  private static string[] HighProduct => [
    "  ulong leftLow = left & 0xFFFFFFFFUL;",
    "  ulong leftHigh = left >> 32;",
    "  ulong rightLow = right & 0xFFFFFFFFUL;",
    "  ulong rightHigh = right >> 32;",
    "  ulong middle = leftHigh * rightLow + ((leftLow * rightLow) >> 32);",
    "  ulong cross = leftLow * rightHigh + (middle & 0xFFFFFFFFUL);",
    "  ulong high = leftHigh * rightHigh + (middle >> 32) + (cross >> 32);"
  ];

}
