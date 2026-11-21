// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The parallel bit extract and deposit, and the byte shuffle. Extract and deposit are the
/// parallel-suffix method of Warren, <i>Hacker's Delight</i>, 2nd ed., §7-4 (compress) and §7-5
/// (expand), written out step by step: no loop, no branch and no table depends on the value or the
/// mask, so the time they take does not either. A pattern of up to 32 bits is worked on as a
/// <c>uint</c>, told its width, one of 64 bits as a <c>ulong</c>.
/// </summary>
public static partial class HelperOperations {

  /// <summary>The bits of the lowest <paramref name="bits"/> bits of a value at the ones of a mask, packed to the bottom.</summary>
  /// <param name="value">The pattern, perhaps with bits above the width.</param>
  /// <param name="mask">The mask, perhaps with bits above the width.</param>
  /// <param name="bits">The width, 8, 16 or 32.</param>
  public static uint ParallelBitExtract32(uint value, uint mask, int bits) {
    var m = bits == 32 ? mask : mask & ((1u << bits) - 1u);
    var x = value & m;
    var mk = ~m << 1;

    var mp = PrefixXor32(mk);
    var mv = mp & m;
    m = (m ^ mv) | (mv >> 1);
    var t = x & mv;
    x = (x ^ t) | (t >> 1);
    mk = mk & ~mp;

    mp = PrefixXor32(mk);
    mv = mp & m;
    m = (m ^ mv) | (mv >> 2);
    t = x & mv;
    x = (x ^ t) | (t >> 2);
    mk = mk & ~mp;

    mp = PrefixXor32(mk);
    mv = mp & m;
    m = (m ^ mv) | (mv >> 4);
    t = x & mv;
    x = (x ^ t) | (t >> 4);
    mk = mk & ~mp;

    mp = PrefixXor32(mk);
    mv = mp & m;
    m = (m ^ mv) | (mv >> 8);
    t = x & mv;
    x = (x ^ t) | (t >> 8);
    mk = mk & ~mp;

    mp = PrefixXor32(mk);
    mv = mp & m;
    t = x & mv;
    return (x ^ t) | (t >> 16);
  }

  /// <summary>The lowest bits of a value spread to the ones of a mask within the lowest <paramref name="bits"/> bits.</summary>
  /// <param name="value">The pattern, perhaps with bits above the width.</param>
  /// <param name="mask">The mask, perhaps with bits above the width.</param>
  /// <param name="bits">The width, 8, 16 or 32.</param>
  public static uint ParallelBitDeposit32(uint value, uint mask, int bits) {
    var m = bits == 32 ? mask : mask & ((1u << bits) - 1u);
    var original = m;
    var mk = ~m << 1;

    var mp = PrefixXor32(mk);
    var mv0 = mp & m;
    m = (m ^ mv0) | (mv0 >> 1);
    mk = mk & ~mp;

    mp = PrefixXor32(mk);
    var mv1 = mp & m;
    m = (m ^ mv1) | (mv1 >> 2);
    mk = mk & ~mp;

    mp = PrefixXor32(mk);
    var mv2 = mp & m;
    m = (m ^ mv2) | (mv2 >> 4);
    mk = mk & ~mp;

    mp = PrefixXor32(mk);
    var mv3 = mp & m;
    m = (m ^ mv3) | (mv3 >> 8);
    mk = mk & ~mp;

    mp = PrefixXor32(mk);
    var mv4 = mp & m;

    var x = value;
    x = (x & ~mv4) | ((x << 16) & mv4);
    x = (x & ~mv3) | ((x << 8) & mv3);
    x = (x & ~mv2) | ((x << 4) & mv2);
    x = (x & ~mv1) | ((x << 2) & mv1);
    x = (x & ~mv0) | ((x << 1) & mv0);
    return x & original;
  }

  /// <summary>The bits of a <c>ulong</c> at the ones of a mask, packed to the bottom.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="mask">The mask.</param>
  public static ulong ParallelBitExtract64(ulong value, ulong mask) {
    var m = mask;
    var x = value & m;
    var mk = ~m << 1;

    var mp = PrefixXor64(mk);
    var mv = mp & m;
    m = (m ^ mv) | (mv >> 1);
    var t = x & mv;
    x = (x ^ t) | (t >> 1);
    mk = mk & ~mp;

    mp = PrefixXor64(mk);
    mv = mp & m;
    m = (m ^ mv) | (mv >> 2);
    t = x & mv;
    x = (x ^ t) | (t >> 2);
    mk = mk & ~mp;

    mp = PrefixXor64(mk);
    mv = mp & m;
    m = (m ^ mv) | (mv >> 4);
    t = x & mv;
    x = (x ^ t) | (t >> 4);
    mk = mk & ~mp;

    mp = PrefixXor64(mk);
    mv = mp & m;
    m = (m ^ mv) | (mv >> 8);
    t = x & mv;
    x = (x ^ t) | (t >> 8);
    mk = mk & ~mp;

    mp = PrefixXor64(mk);
    mv = mp & m;
    m = (m ^ mv) | (mv >> 16);
    t = x & mv;
    x = (x ^ t) | (t >> 16);
    mk = mk & ~mp;

    mp = PrefixXor64(mk);
    mv = mp & m;
    t = x & mv;
    return (x ^ t) | (t >> 32);
  }

  /// <summary>The lowest bits of a <c>ulong</c> spread to the ones of a mask.</summary>
  /// <param name="value">The pattern.</param>
  /// <param name="mask">The mask.</param>
  public static ulong ParallelBitDeposit64(ulong value, ulong mask) {
    var m = mask;
    var mk = ~m << 1;

    var mp = PrefixXor64(mk);
    var mv0 = mp & m;
    m = (m ^ mv0) | (mv0 >> 1);
    mk = mk & ~mp;

    mp = PrefixXor64(mk);
    var mv1 = mp & m;
    m = (m ^ mv1) | (mv1 >> 2);
    mk = mk & ~mp;

    mp = PrefixXor64(mk);
    var mv2 = mp & m;
    m = (m ^ mv2) | (mv2 >> 4);
    mk = mk & ~mp;

    mp = PrefixXor64(mk);
    var mv3 = mp & m;
    m = (m ^ mv3) | (mv3 >> 8);
    mk = mk & ~mp;

    mp = PrefixXor64(mk);
    var mv4 = mp & m;
    m = (m ^ mv4) | (mv4 >> 16);
    mk = mk & ~mp;

    mp = PrefixXor64(mk);
    var mv5 = mp & m;

    var x = value;
    x = (x & ~mv5) | ((x << 32) & mv5);
    x = (x & ~mv4) | ((x << 16) & mv4);
    x = (x & ~mv3) | ((x << 8) & mv3);
    x = (x & ~mv2) | ((x << 4) & mv2);
    x = (x & ~mv1) | ((x << 2) & mv1);
    x = (x & ~mv0) | ((x << 1) & mv0);
    return x & mask;
  }

  /// <summary>
  /// The bytes of a <c>ulong</c> table chosen by the bytes of a <c>ulong</c> of indices within one group
  /// of 8 bytes: byte <c>i</c> of the result is 0 where bit 7 of index <c>i</c> is set, else byte
  /// <c>index mod 8</c> of the table.
  /// </summary>
  /// <param name="table">The table.</param>
  /// <param name="indices">The indices, one per byte.</param>
  /// <param name="group">The bytes of a group, which for 8 bytes can only be 8.</param>
  /// <exception cref="ArithmeticException">The group is not 8.</exception>
  public static ulong ByteShuffle64(ulong table, ulong indices, int group) {
    if (group != 8)
      throw new ArithmeticException("invalid operation");

    var result = 0UL;
    for (var index = 0; index < 8; ++index) {
      var selector = (int)((indices >> (8 * index)) & 0xFFUL);
      var chosen = (table >> (8 * (selector & 7))) & 0xFFUL;
      result = result | ((selector < 0x80 ? chosen : 0UL) << (8 * index));
    }

    return result;
  }

  /// <summary>Every bit XORed with all bits below it: the parallel suffix of a <c>uint</c>.</summary>
  private static uint PrefixXor32(uint value) {
    var v = value ^ (value << 1);
    v = v ^ (v << 2);
    v = v ^ (v << 4);
    v = v ^ (v << 8);
    return v ^ (v << 16);
  }

  /// <summary>Every bit XORed with all bits below it: the parallel suffix of a <c>ulong</c>.</summary>
  private static ulong PrefixXor64(ulong value) {
    var v = value ^ (value << 1);
    v = v ^ (v << 2);
    v = v ^ (v << 4);
    v = v ^ (v << 8);
    v = v ^ (v << 16);
    return v ^ (v << 32);
  }

}
