// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The Boolean functions beyond <c>And</c>, <c>Or</c>, <c>Xor</c> and the complement. Every bit is
/// computed alone, so a pattern of up to 32 bits is worked on as a <c>uint</c> and one of 64 bits as a
/// <c>ulong</c>; the bits above a narrower width are cut off by the conversion of the result. The
/// three-input function of a truth table is the Shannon expansion on its first operand: the function of
/// the other two that the high nibble of the table gives where the first has a 1, the one of the low
/// nibble where it has a 0.
/// </summary>
public static partial class HelperOperations {

  /// <summary><c>~(a &amp; b)</c> of a <c>uint</c>.</summary>
  /// <param name="a">The first pattern.</param>
  /// <param name="b">The second pattern.</param>
  public static uint Nand32(uint a, uint b) => ~(a & b);

  /// <summary><c>~(a &amp; b)</c> of a <c>ulong</c>.</summary>
  /// <param name="a">The first pattern.</param>
  /// <param name="b">The second pattern.</param>
  public static ulong Nand64(ulong a, ulong b) => ~(a & b);

  /// <summary><c>~(a | b)</c> of a <c>uint</c>.</summary>
  /// <param name="a">The first pattern.</param>
  /// <param name="b">The second pattern.</param>
  public static uint Nor32(uint a, uint b) => ~(a | b);

  /// <summary><c>~(a | b)</c> of a <c>ulong</c>.</summary>
  /// <param name="a">The first pattern.</param>
  /// <param name="b">The second pattern.</param>
  public static ulong Nor64(ulong a, ulong b) => ~(a | b);

  /// <summary><c>~(a ^ b)</c> of a <c>uint</c>.</summary>
  /// <param name="a">The first pattern.</param>
  /// <param name="b">The second pattern.</param>
  public static uint Xnor32(uint a, uint b) => ~(a ^ b);

  /// <summary><c>~(a ^ b)</c> of a <c>ulong</c>.</summary>
  /// <param name="a">The first pattern.</param>
  /// <param name="b">The second pattern.</param>
  public static ulong Xnor64(ulong a, ulong b) => ~(a ^ b);

  /// <summary><c>a &amp; ~b</c> of a <c>uint</c>.</summary>
  /// <param name="a">The first pattern.</param>
  /// <param name="b">The pattern whose bits are cleared.</param>
  public static uint AndNot32(uint a, uint b) => a & ~b;

  /// <summary><c>a &amp; ~b</c> of a <c>ulong</c>.</summary>
  /// <param name="a">The first pattern.</param>
  /// <param name="b">The pattern whose bits are cleared.</param>
  public static ulong AndNot64(ulong a, ulong b) => a & ~b;

  /// <summary><c>a | ~b</c> of a <c>uint</c>.</summary>
  /// <param name="a">The first pattern.</param>
  /// <param name="b">The pattern that is complemented.</param>
  public static uint OrNot32(uint a, uint b) => a | ~b;

  /// <summary><c>a | ~b</c> of a <c>ulong</c>.</summary>
  /// <param name="a">The first pattern.</param>
  /// <param name="b">The pattern that is complemented.</param>
  public static ulong OrNot64(ulong a, ulong b) => a | ~b;

  /// <summary><c>~a | b</c> of a <c>uint</c>.</summary>
  /// <param name="a">The premise.</param>
  /// <param name="b">The conclusion.</param>
  public static uint Implies32(uint a, uint b) => ~a | b;

  /// <summary><c>~a | b</c> of a <c>ulong</c>.</summary>
  /// <param name="a">The premise.</param>
  /// <param name="b">The conclusion.</param>
  public static ulong Implies64(ulong a, ulong b) => ~a | b;

  /// <summary>The bits of <paramref name="a"/> where the mask has a 1, of <paramref name="b"/> where it has a 0, of a <c>uint</c>.</summary>
  /// <param name="mask">The selecting pattern.</param>
  /// <param name="a">The pattern selected by a 1.</param>
  /// <param name="b">The pattern selected by a 0.</param>
  public static uint BitSelect32(uint mask, uint a, uint b) => (mask & a) | (~mask & b);

  /// <summary>The bits of <paramref name="a"/> where the mask has a 1, of <paramref name="b"/> where it has a 0, of a <c>ulong</c>.</summary>
  /// <param name="mask">The selecting pattern.</param>
  /// <param name="a">The pattern selected by a 1.</param>
  /// <param name="b">The pattern selected by a 0.</param>
  public static ulong BitSelect64(ulong mask, ulong a, ulong b) => (mask & a) | (~mask & b);

  /// <summary>The bits set in at least two of three <c>uint</c> patterns.</summary>
  /// <param name="a">The first pattern.</param>
  /// <param name="b">The second pattern.</param>
  /// <param name="c">The third pattern.</param>
  public static uint Majority32(uint a, uint b, uint c) => (a & b) | (c & (a | b));

  /// <summary>The bits set in at least two of three <c>ulong</c> patterns.</summary>
  /// <param name="a">The first pattern.</param>
  /// <param name="b">The second pattern.</param>
  /// <param name="c">The third pattern.</param>
  public static ulong Majority64(ulong a, ulong b, ulong c) => (a & b) | (c & (a | b));

  /// <summary>The three-input function of a truth table on <c>uint</c> patterns.</summary>
  /// <param name="a">The first pattern, the high bit of the index into the table.</param>
  /// <param name="b">The second pattern.</param>
  /// <param name="c">The third pattern, the low bit of the index.</param>
  /// <param name="table">The truth table, 0 to 255.</param>
  /// <exception cref="ArithmeticException">The table lies outside 0 to 255.</exception>
  public static uint TernaryLogic32(uint a, uint b, uint c, int table) {
    CheckTable(table);
    return (a & BinaryFunction32(table >> 4, b, c)) | (~a & BinaryFunction32(table & 15, b, c));
  }

  /// <summary>The three-input function of a truth table on <c>ulong</c> patterns.</summary>
  /// <param name="a">The first pattern, the high bit of the index into the table.</param>
  /// <param name="b">The second pattern.</param>
  /// <param name="c">The third pattern, the low bit of the index.</param>
  /// <param name="table">The truth table, 0 to 255.</param>
  /// <exception cref="ArithmeticException">The table lies outside 0 to 255.</exception>
  public static ulong TernaryLogic64(ulong a, ulong b, ulong c, int table) {
    CheckTable(table);
    return (a & BinaryFunction64(table >> 4, b, c)) | (~a & BinaryFunction64(table & 15, b, c));
  }

  /// <summary>The two-input function of a nibble: its bit <c>b_i·2 + c_i</c> is bit <c>i</c> of the result, a disjunction of the minterms it has.</summary>
  private static uint BinaryFunction32(int nibble, uint b, uint c) {
    var result = 0u;
    if ((nibble & 8) != 0)
      result |= b & c;
    if ((nibble & 4) != 0)
      result |= b & ~c;
    if ((nibble & 2) != 0)
      result |= ~b & c;
    if ((nibble & 1) != 0)
      result |= ~b & ~c;

    return result;
  }

  /// <summary>The two-input function of a nibble on <c>ulong</c> patterns.</summary>
  private static ulong BinaryFunction64(int nibble, ulong b, ulong c) {
    var result = 0UL;
    if ((nibble & 8) != 0)
      result |= b & c;
    if ((nibble & 4) != 0)
      result |= b & ~c;
    if ((nibble & 2) != 0)
      result |= ~b & c;
    if ((nibble & 1) != 0)
      result |= ~b & ~c;

    return result;
  }

  private static void CheckTable(int table) {
    if (table < 0 || table > 255)
      throw new ArithmeticException("invalid operation");
  }

}
