namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerHelpers {

  /// <summary>The bytes of the pattern of a value of 8 to 64 bits in the opposite order.</summary>
  public static CSharpHelper ByteSwapBits { get; } = new("ByteSwapBits", "ulong", "ulong value, int bits", [
    "ulong result = 0UL;",
    "for (int shift = 0; shift < bits; shift += 8) {",
    "  result = (result << 8) | ((value >> shift) & 0xFFUL);",
    "}",
    "return result;"
  ]);

  /// <summary>The bits of the pattern of a value of up to 64 bits in the opposite order, one bit at a time.</summary>
  public static CSharpHelper BitReverseBits { get; } = new("BitReverseBits", "ulong", "ulong value, int bits", [
    "ulong result = 0UL;",
    "for (int index = 0; index < bits; ++index) {",
    "  result = (result << 1) | (value & 1UL);",
    "  value >>= 1;",
    "}",
    "return result;"
  ]);

}
