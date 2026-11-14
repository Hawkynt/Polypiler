// SPDX-License-Identifier: MIT
namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerHelpers {

  /// <summary>
  /// The bytes of the pattern of a value of 8 to 64 bits in the opposite order: the 64 bits swapped in
  /// three steps, then the swapped bytes of the width shifted down - as cheap as the shifts it stands for.
  /// </summary>
  public static CSharpHelper ByteSwapBits { get; } = new("ByteSwapBits", "ulong", "ulong value, int bits", [
    "value = ((value & 0x00FF00FF00FF00FFUL) << 8) | ((value >> 8) & 0x00FF00FF00FF00FFUL);",
    "value = ((value & 0x0000FFFF0000FFFFUL) << 16) | ((value >> 16) & 0x0000FFFF0000FFFFUL);",
    "return ((value << 32) | (value >> 32)) >> (64 - bits);"
  ]);

  /// <summary>The bits of the pattern of a value of up to 64 bits in the opposite order: the swap cascade on 64 bits, the width shifted down.</summary>
  public static CSharpHelper BitReverseBits { get; } = new("BitReverseBits", "ulong", "ulong value, int bits", [
    "value = ((value & 0x5555555555555555UL) << 1) | ((value >> 1) & 0x5555555555555555UL);",
    "value = ((value & 0x3333333333333333UL) << 2) | ((value >> 2) & 0x3333333333333333UL);",
    "value = ((value & 0x0F0F0F0F0F0F0F0FUL) << 4) | ((value >> 4) & 0x0F0F0F0F0F0F0F0FUL);",
    "value = ((value & 0x00FF00FF00FF00FFUL) << 8) | ((value >> 8) & 0x00FF00FF00FF00FFUL);",
    "value = ((value & 0x0000FFFF0000FFFFUL) << 16) | ((value >> 16) & 0x0000FFFF0000FFFFUL);",
    "return ((value << 32) | (value >> 32)) >> (64 - bits);"
  ]);

}
