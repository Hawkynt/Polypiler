using System.Collections.Generic;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>
  /// Fields of bits and single bits of a value of every integer type; a start, a length and a
  /// position are <c>i32</c>, and a field that does not lie within the width traps.
  /// </summary>
  private static IEnumerable<IntegerOperation> BitFields => [
    Same(IrIntrinsicOp.ExtractBits, _integers, type => [type, IrType.I32, IrType.I32],
      (_, types, arguments) => ExtractBits(types[0], Long(arguments, 0), Count(arguments, 1), Count(arguments, 2))),
    Same(IrIntrinsicOp.InsertBits, _integers, type => [type, type, IrType.I32, IrType.I32],
      (_, types, arguments) => InsertBits(types[0], Long(arguments, 0), Long(arguments, 1), Count(arguments, 2), Count(arguments, 3))),
    Fixed(IrIntrinsicOp.TestBit, IrType.Bool, _integers, type => [type, IrType.I32], (_, types, arguments) => TestBit(types[0], Long(arguments, 0), Count(arguments, 1))),
    Same(IrIntrinsicOp.SetBit, _integers, type => [type, IrType.I32], (_, types, arguments) => SetBit(types[0], Long(arguments, 0), Count(arguments, 1))),
    Same(IrIntrinsicOp.ClearBit, _integers, type => [type, IrType.I32], (_, types, arguments) => ClearBit(types[0], Long(arguments, 0), Count(arguments, 1))),
    Same(IrIntrinsicOp.ToggleBit, _integers, type => [type, IrType.I32], (_, types, arguments) => ToggleBit(types[0], Long(arguments, 0), Count(arguments, 1)))
  ];

  /// <summary>
  /// The <paramref name="length"/> bits from bit <paramref name="start"/> on, moved to the bottom
  /// with zeros above them, read as a value of the type. A length of 0 extracts 0.
  /// </summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <param name="start">The position of the lowest bit of the field.</param>
  /// <param name="length">The number of bits of the field.</param>
  /// <returns>The field.</returns>
  /// <exception cref="IrTrapException">The field does not lie within the width: an invalid operation.</exception>
  public static long ExtractBits(IrType type, long value, int start, int length) {
    Field(type, start, length);
    return length == 0 ? 0 : Value(type, (Pattern(type, value) >> start) & Ones(length));
  }

  /// <summary>
  /// The value with the field of <paramref name="length"/> bits from bit <paramref name="start"/>
  /// on replaced by the lowest bits of <paramref name="field"/>.
  /// </summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <param name="field">The bits to insert, taken from its bottom.</param>
  /// <param name="start">The position of the lowest bit of the field.</param>
  /// <param name="length">The number of bits of the field.</param>
  /// <returns>The value with the field replaced.</returns>
  /// <exception cref="IrTrapException">The field does not lie within the width: an invalid operation.</exception>
  public static long InsertBits(IrType type, long value, long field, int start, int length) {
    Field(type, start, length);
    if (length == 0)
      return value;

    var mask = Ones(length) << start;
    return Value(type, (Pattern(type, value) & ~mask) | ((Pattern(type, field) << start) & mask));
  }

  /// <summary>Whether one bit of the value is set.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <param name="position">The position of the bit, 0 for the lowest.</param>
  /// <returns><see langword="true"/> when the bit is 1.</returns>
  /// <exception cref="IrTrapException">The position is not within the width: an invalid operation.</exception>
  public static bool TestBit(IrType type, long value, int position) => ((Pattern(type, value) >> Position(type, position)) & 1) == 1;

  /// <summary>The value with one bit set.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <param name="position">The position of the bit.</param>
  /// <returns>The value with the bit 1.</returns>
  /// <exception cref="IrTrapException">The position is not within the width: an invalid operation.</exception>
  public static long SetBit(IrType type, long value, int position) => Value(type, Pattern(type, value) | (1UL << Position(type, position)));

  /// <summary>The value with one bit cleared.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <param name="position">The position of the bit.</param>
  /// <returns>The value with the bit 0.</returns>
  /// <exception cref="IrTrapException">The position is not within the width: an invalid operation.</exception>
  public static long ClearBit(IrType type, long value, int position) => Value(type, Pattern(type, value) & ~(1UL << Position(type, position)));

  /// <summary>The value with one bit inverted.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <param name="position">The position of the bit.</param>
  /// <returns>The value with the bit flipped.</returns>
  /// <exception cref="IrTrapException">The position is not within the width: an invalid operation.</exception>
  public static long ToggleBit(IrType type, long value, int position) => Value(type, Pattern(type, value) ^ (1UL << Position(type, position)));

  /// <summary>Requires a field to lie within the width: a start and a length that are not negative and do not reach past the top.</summary>
  private static void Field(IrType type, int start, int length) => Require(start >= 0 && length >= 0 && (long)start + length <= Width(type));

  /// <summary>A position within the width, which it requires.</summary>
  private static int Position(IrType type, int position) {
    Require(position >= 0 && position < Width(type));
    return position;
  }

}
