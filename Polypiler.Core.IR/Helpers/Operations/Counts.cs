// SPDX-License-Identifier: MIT
namespace Polypiler.Core.IR;

/// <summary>
/// The bit counts. They follow Warren, <i>Hacker's Delight</i>, 2nd edition: the population count of
/// §5-1, the binary search for the leading zeros of §5-3 and for the trailing zeros of §5-4. A count
/// of 64 bits is the counts of its two halves; one of 8 or 16 bits is the count of 32 bits of the
/// pattern, corrected for the width.
/// </summary>
public static partial class HelperOperations {

  /// <summary>The number of set bits of a <c>uint</c>.</summary>
  /// <param name="value">The pattern.</param>
  public static int PopCount32(uint value) {
    value -= (value >> 1) & 0x55555555u;
    value = (value & 0x33333333u) + ((value >> 2) & 0x33333333u);
    value = (value + (value >> 4)) & 0x0F0F0F0Fu;
    return (int)((value * 0x01010101u) >> 24);
  }

  /// <summary>The number of set bits of a <c>ulong</c>.</summary>
  /// <param name="value">The pattern.</param>
  public static int PopCount64(ulong value) => PopCount32((uint)value) + PopCount32((uint)(value >> 32));

  /// <summary>The number of set bits in the lowest <paramref name="bits"/> bits.</summary>
  /// <param name="value">The pattern, perhaps with bits above the width.</param>
  /// <param name="bits">The width, 8 or 16.</param>
  public static int PopCountNarrow(uint value, int bits) => PopCount32(value & ((1u << bits) - 1u));

  /// <summary>The number of zero bits above the highest set bit of a <c>uint</c>; 32 for 0.</summary>
  /// <param name="value">The pattern.</param>
  public static int LeadingZeroCount32(uint value) {
    if (value == 0u)
      return 32;

    var count = 0;
    if (value <= 0x0000FFFFu) {
      count += 16;
      value <<= 16;
    }

    if (value <= 0x00FFFFFFu) {
      count += 8;
      value <<= 8;
    }

    if (value <= 0x0FFFFFFFu) {
      count += 4;
      value <<= 4;
    }

    if (value <= 0x3FFFFFFFu) {
      count += 2;
      value <<= 2;
    }

    return value <= 0x7FFFFFFFu ? count + 1 : count;
  }

  /// <summary>The number of zero bits above the highest set bit of a <c>ulong</c>; 64 for 0.</summary>
  /// <param name="value">The pattern.</param>
  public static int LeadingZeroCount64(ulong value) {
    var high = (uint)(value >> 32);
    return high != 0u ? LeadingZeroCount32(high) : 32 + LeadingZeroCount32((uint)value);
  }

  /// <summary>The number of zero bits above the highest set bit within the lowest <paramref name="bits"/> bits; the width for 0.</summary>
  /// <param name="value">The pattern, perhaps with bits above the width.</param>
  /// <param name="bits">The width, 8 or 16.</param>
  public static int LeadingZeroCountNarrow(uint value, int bits) => LeadingZeroCount32(value & ((1u << bits) - 1u)) - (32 - bits);

  /// <summary>The number of zero bits below the lowest set bit of a <c>uint</c>; 32 for 0.</summary>
  /// <param name="value">The pattern.</param>
  public static int TrailingZeroCount32(uint value) {
    if (value == 0u)
      return 32;

    var count = 0;
    if ((value & 0x0000FFFFu) == 0u) {
      count += 16;
      value >>= 16;
    }

    if ((value & 0x000000FFu) == 0u) {
      count += 8;
      value >>= 8;
    }

    if ((value & 0x0000000Fu) == 0u) {
      count += 4;
      value >>= 4;
    }

    if ((value & 0x00000003u) == 0u) {
      count += 2;
      value >>= 2;
    }

    return (value & 0x00000001u) == 0u ? count + 1 : count;
  }

  /// <summary>The number of zero bits below the lowest set bit of a <c>ulong</c>; 64 for 0.</summary>
  /// <param name="value">The pattern.</param>
  public static int TrailingZeroCount64(ulong value) {
    var low = (uint)value;
    return low != 0u ? TrailingZeroCount32(low) : 32 + TrailingZeroCount32((uint)(value >> 32));
  }

  /// <summary>The number of zero bits below the lowest set bit within the lowest <paramref name="bits"/> bits; the width for 0.</summary>
  /// <param name="value">The pattern, perhaps with bits above the width.</param>
  /// <param name="bits">The width, 8 or 16.</param>
  public static int TrailingZeroCountNarrow(uint value, int bits) => TrailingZeroCount32(value | (1u << bits));

  /// <summary>The position of the highest set bit of a <c>uint</c>; 0 for 0.</summary>
  /// <param name="value">The pattern.</param>
  public static int Log2Of32(uint value) => 31 - LeadingZeroCount32(value | 1u);

  /// <summary>The position of the highest set bit of a <c>ulong</c>; 0 for 0.</summary>
  /// <param name="value">The pattern.</param>
  public static int Log2Of64(ulong value) => 63 - LeadingZeroCount64(value | 1UL);

  /// <summary>The position of the highest set bit within the lowest <paramref name="bits"/> bits; 0 for 0.</summary>
  /// <param name="value">The pattern, perhaps with bits above the width.</param>
  /// <param name="bits">The width, 8 or 16.</param>
  public static int Log2Narrow(uint value, int bits) => Log2Of32(value & ((1u << bits) - 1u));

}
