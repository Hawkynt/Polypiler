// SPDX-License-Identifier: MIT
namespace Polypiler.Core.IR;

/// <summary>
/// The upper half of a product in twice the width, from four partial products of half the width as
/// Warren, <i>Hacker's Delight</i>, 2nd edition, §8-2 forms them, none of which overflows; the signed
/// one corrects the unsigned one by the operands that are negative.
/// </summary>
public static partial class HelperOperations {

  /// <summary>The upper 32 bits of the product of two <c>uint</c>.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  public static uint MulHighUnsigned32(uint left, uint right) {
    var leftLow = left & 0xFFFFu;
    var leftHigh = left >> 16;
    var rightLow = right & 0xFFFFu;
    var rightHigh = right >> 16;
    var middle = leftHigh * rightLow + ((leftLow * rightLow) >> 16);
    var cross = leftLow * rightHigh + (middle & 0xFFFFu);
    return leftHigh * rightHigh + (middle >> 16) + (cross >> 16);
  }

  /// <summary>The upper 32 bits of the product of two <c>int</c>.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  public static int MulHighSigned32(int left, int right)
    => (int)(MulHighUnsigned32((uint)left, (uint)right) - (left < 0 ? (uint)right : 0u) - (right < 0 ? (uint)left : 0u));

  /// <summary>The upper 64 bits of the product of two <c>ulong</c>.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  public static ulong MulHighUnsigned64(ulong left, ulong right) {
    var leftLow = left & 0xFFFFFFFFUL;
    var leftHigh = left >> 32;
    var rightLow = right & 0xFFFFFFFFUL;
    var rightHigh = right >> 32;
    var middle = leftHigh * rightLow + ((leftLow * rightLow) >> 32);
    var cross = leftLow * rightHigh + (middle & 0xFFFFFFFFUL);
    return leftHigh * rightHigh + (middle >> 32) + (cross >> 32);
  }

  /// <summary>The upper 64 bits of the product of two <c>long</c>.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  public static long MulHighSigned64(long left, long right) {
    var high = MulHighUnsigned64((ulong)left, (ulong)right);
    if (left < 0L)
      high -= (ulong)right;

    if (right < 0L)
      high -= (ulong)left;

    return (long)high;
  }

}
