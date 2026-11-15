// SPDX-License-Identifier: MIT
namespace Polypiler.Core.IR;

/// <summary>
/// Whether integer arithmetic overflows its type, as the saturating arithmetic tells it: by the signs
/// of the operands and the wrapped result, by the bounds of a narrower type, or by the quotient that
/// undoes a product.
/// </summary>
public static partial class HelperOperations {

  /// <summary>Whether the sum of two values of a signed type of up to 32 bits lies outside its bounds.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  /// <param name="low">The smallest value of the type.</param>
  /// <param name="high">The largest value of the type.</param>
  public static bool AddOverflowsSigned32(int left, int right, int low, int high) {
    var sum = left + right;
    return ((left ^ sum) & (right ^ sum)) < 0 || sum < low || sum > high;
  }

  /// <summary>Whether the difference of two values of a signed type of up to 32 bits lies outside its bounds.</summary>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  /// <param name="low">The smallest value of the type.</param>
  /// <param name="high">The largest value of the type.</param>
  public static bool SubOverflowsSigned32(int left, int right, int low, int high) {
    var difference = left - right;
    return ((left ^ right) & (left ^ difference)) < 0 || difference < low || difference > high;
  }

  /// <summary>Whether the product of two values of a signed type of up to 32 bits lies outside its bounds.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  /// <param name="low">The smallest value of the type.</param>
  /// <param name="high">The largest value of the type.</param>
  public static bool MulOverflowsSigned32(int left, int right, int low, int high) {
    var product = (long)left * right;
    return product < low || product > high;
  }

  /// <summary>Whether the sum of two values of an unsigned type of up to 32 bits lies above its largest value.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  /// <param name="high">The largest value of the type.</param>
  public static bool AddOverflowsUnsigned32(uint left, uint right, uint high) {
    var sum = left + right;
    return sum < left || sum > high;
  }

  /// <summary>Whether the product of two values of an unsigned type of up to 32 bits lies above its largest value.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  /// <param name="high">The largest value of the type.</param>
  public static bool MulOverflowsUnsigned32(uint left, uint right, uint high) => left != 0u && right > high / left;

  /// <summary>Whether the sum of two <c>long</c> overflows.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  public static bool AddOverflowsSigned64(long left, long right) {
    var sum = left + right;
    return ((left ^ sum) & (right ^ sum)) < 0L;
  }

  /// <summary>Whether the difference of two <c>long</c> overflows.</summary>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  public static bool SubOverflowsSigned64(long left, long right) {
    var difference = left - right;
    return ((left ^ right) & (left ^ difference)) < 0L;
  }

  /// <summary>Whether the product of two <c>long</c> overflows.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  public static bool MulOverflowsSigned64(long left, long right) {
    if (left == 0L || right == 0L)
      return false;

    return (left == -1L && right == long.MinValue) || (right == -1L && left == long.MinValue) || (left * right) / right != left;
  }

  /// <summary>Whether the sum of two <c>ulong</c> overflows.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  public static bool AddOverflowsUnsigned64(ulong left, ulong right) => left + right < left;

  /// <summary>Whether the product of two <c>ulong</c> overflows.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  public static bool MulOverflowsUnsigned64(ulong left, ulong right) => left != 0UL && right > ulong.MaxValue / left;

  /// <summary>Whether adding two <c>ulong</c> and a carry carries out of 64 bits.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  /// <param name="carry">The carry into the sum.</param>
  public static bool CarryOut64(ulong left, ulong right, bool carry) {
    var sum = left + right;
    return sum < left || (carry && sum == ulong.MaxValue);
  }

  /// <summary>Whether subtracting a <c>ulong</c> and a borrow from another borrows from above 64 bits.</summary>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  /// <param name="borrow">The borrow into the difference.</param>
  public static bool BorrowOut64(ulong left, ulong right, bool borrow) => left < right || (borrow && left == right);

}
