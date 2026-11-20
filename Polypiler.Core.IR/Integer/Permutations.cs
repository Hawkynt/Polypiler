using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>The group sizes in bytes a byte shuffle selects within: those of <c>PSHUFB</c> and its wider forms.</summary>
  private static readonly int[] _groups = [8, 16, 32, 64];

  /// <summary>
  /// Bit permutations under a mask and byte shuffles: the parallel bit extract and deposit of every
  /// integer type, and the byte shuffle of the unsigned types of at least 64 bits, which is <c>u64</c>
  /// until the IR has wider integers.
  /// </summary>
  private static IEnumerable<IntegerOperation> Permutations => [
    Same(IrIntrinsicOp.ParallelBitExtract, _integers, type => [type, type], (_, types, arguments) => ParallelBitExtract(types[0], Long(arguments, 0), Long(arguments, 1))),
    Same(IrIntrinsicOp.ParallelBitDeposit, _integers, type => [type, type], (_, types, arguments) => ParallelBitDeposit(types[0], Long(arguments, 0), Long(arguments, 1))),
    Same(IrIntrinsicOp.ByteShuffle, [.. _unsigned.Where(type => type.Bits >= 64)], type => [type, type, IrType.I32],
      (_, types, arguments) => ByteShuffle(types[0], Long(arguments, 0), Long(arguments, 1), Count(arguments, 2)))
  ];

  /// <summary>
  /// The bits of a value at the positions of the ones of a mask, from the lowest position up, packed
  /// to the bottom of the result with zeros above them. With <c>m_0 &lt; m_1 &lt; …</c> the positions
  /// of the ones of the mask, bit <c>j</c> of the result is bit <c>m_j</c> of the value.
  /// </summary>
  /// <param name="type">An integer type; its signedness is ignored.</param>
  /// <param name="value">The value.</param>
  /// <param name="mask">The mask.</param>
  /// <returns>The extracted bits.</returns>
  public static long ParallelBitExtract(IrType type, long value, long mask) {
    var bits = Pattern(type, value);
    var selected = Pattern(type, mask);
    var result = 0UL;
    var next = 0;
    for (var position = 0; position < Width(type); ++position)
      if (((selected >> position) & 1) == 1)
        result |= ((bits >> position) & 1) << next++;

    return Value(type, result);
  }

  /// <summary>
  /// The lowest bits of a value, from the lowest up, spread to the positions of the ones of a mask,
  /// zeros elsewhere. With <c>m_0 &lt; m_1 &lt; …</c> the positions of the ones of the mask, bit
  /// <c>m_j</c> of the result is bit <c>j</c> of the value.
  /// </summary>
  /// <param name="type">An integer type; its signedness is ignored.</param>
  /// <param name="value">The value.</param>
  /// <param name="mask">The mask.</param>
  /// <returns>The deposited bits.</returns>
  public static long ParallelBitDeposit(IrType type, long value, long mask) {
    var bits = Pattern(type, value);
    var selected = Pattern(type, mask);
    var result = 0UL;
    var next = 0;
    for (var position = 0; position < Width(type); ++position)
      if (((selected >> position) & 1) == 1)
        result |= ((bits >> next++) & 1) << position;

    return Value(type, result);
  }

  /// <summary>
  /// The bytes of a table chosen by the bytes of the indices, as <c>PSHUFB</c> chooses them: byte
  /// <c>i</c> of the result is 0 where bit 7 of byte <c>i</c> of the indices is set, otherwise byte
  /// <c>base + (index mod group)</c> of the table, where <c>base</c> is the first byte of the group that
  /// holds byte <c>i</c>. The bits of an index between the selector and bit 7 are ignored, and bytes
  /// count from the least significant.
  /// </summary>
  /// <param name="type">An unsigned integer type of at least 64 bits.</param>
  /// <param name="table">The table.</param>
  /// <param name="indices">The indices, one per byte.</param>
  /// <param name="group">The bytes of a group: 8, 16, 32 or 64, dividing the bytes of the type.</param>
  /// <returns>The shuffled bytes.</returns>
  /// <exception cref="IrTrapException">The group is not one of those: an invalid operation.</exception>
  public static long ByteShuffle(IrType type, long table, long indices, int group) {
    var bytes = Width(type) / 8;
    Require(_groups.Contains(group) && bytes % group == 0);
    var source = Pattern(type, table);
    var selectors = Pattern(type, indices);
    var result = 0UL;
    for (var index = 0; index < bytes; ++index) {
      var selector = (int)((selectors >> (8 * index)) & 0xFF);
      if (selector >= 0x80)
        continue;

      var chosen = group * (index / group) + selector % group;
      result |= ((source >> (8 * chosen)) & 0xFF) << (8 * index);
    }

    return Value(type, result);
  }

}
