using System.Collections.Generic;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>The rotates: <c>(value, count)</c> of every integer type.</summary>
  private static IEnumerable<IntegerOperation> Rotate => [
    Same(IrIntrinsicOp.RotateLeft, _integers, type => [type, IrType.I32], (_, types, arguments) => RotateLeft(types[0], Long(arguments, 0), Count(arguments, 1))),
    Same(IrIntrinsicOp.RotateRight, _integers, type => [type, IrType.I32], (_, types, arguments) => RotateRight(types[0], Long(arguments, 0), Count(arguments, 1)))
  ];

  /// <summary>
  /// The bits of a value rotated towards the top by the count modulo the width; a negative count
  /// rotates the other way. A signed value is rotated as its bit pattern.
  /// </summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <param name="count">Any count.</param>
  /// <returns>The rotated value.</returns>
  public static long RotateLeft(IrType type, long value, int count) {
    var width = Width(type);
    var by = count & (width - 1);
    var pattern = Pattern(type, value);
    return by == 0 ? value : Value(type, (pattern << by) | (pattern >> (width - by)));
  }

  /// <summary>The bits of a value rotated towards the bottom by the count modulo the width.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <param name="count">Any count.</param>
  /// <returns>The rotated value.</returns>
  public static long RotateRight(IrType type, long value, int count) => RotateLeft(type, value, -(count & (Width(type) - 1)));

}
