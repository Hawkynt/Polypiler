using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>
  /// Integers of 16, 32 and 64 bits read from and written to an array of bytes at an index, in
  /// either byte order: <c>(array, index)</c> for a load, whose type is the type of the node, and
  /// <c>(array, index, value)</c> for a store, which yields nothing.
  /// </summary>
  private static IEnumerable<IntegerOperation> EndianAccess => [
    Loads(IrIntrinsicOp.LoadLittle, true),
    Loads(IrIntrinsicOp.LoadBig, false),
    Stores(IrIntrinsicOp.StoreLittle, true),
    Stores(IrIntrinsicOp.StoreBig, false)
  ];

  private static IntegerOperation Loads(IrIntrinsicOp op, bool little)
    => new(op, [.. _multibyte.Select(type => (type, (IReadOnlyList<IrType>)[_bytes, IrType.I32]))],
      (result, _, arguments) => Load(result, (byte[])arguments[0], Long(arguments, 1), little), false);

  private static IntegerOperation Stores(IrIntrinsicOp op, bool little)
    => new(op, [.. _multibyte.Select(type => (IrType.Void, (IReadOnlyList<IrType>)[_bytes, IrType.I32, type]))],
      (_, types, arguments) => {
        Store(types[2], (byte[])arguments[0], Long(arguments, 1), Long(arguments, 2), little);
        return null;
      }, false);

  /// <summary>The value of the type whose bytes stand in the array from the index on, lowest first or highest first.</summary>
  /// <param name="type">An integer type of 16, 32 or 64 bits.</param>
  /// <param name="array">The bytes.</param>
  /// <param name="index">The position of the first byte.</param>
  /// <param name="little">Whether the lowest byte comes first; otherwise the highest does.</param>
  /// <returns>The value.</returns>
  /// <exception cref="IrTrapException">A byte of the value lies outside the array: an index out of range.</exception>
  public static long Load(IrType type, byte[] array, long index, bool little) {
    var size = Inside(type, array, index);
    var pattern = 0UL;
    for (var offset = 0; offset < size; ++offset)
      pattern = (pattern << 8) | array[index + (little ? size - 1 - offset : offset)];

    return Value(type, pattern);
  }

  /// <summary>Writes the bytes of a value into the array from the index on, lowest first or highest first.</summary>
  /// <param name="type">An integer type of 16, 32 or 64 bits.</param>
  /// <param name="array">The bytes.</param>
  /// <param name="index">The position of the first byte.</param>
  /// <param name="value">The value.</param>
  /// <param name="little">Whether the lowest byte comes first; otherwise the highest does.</param>
  /// <exception cref="IrTrapException">A byte of the value lies outside the array: an index out of range, before anything is written.</exception>
  public static void Store(IrType type, byte[] array, long index, long value, bool little) {
    var size = Inside(type, array, index);
    var pattern = Pattern(type, value);
    for (var offset = 0; offset < size; ++offset)
      array[index + (little ? offset : size - 1 - offset)] = (byte)(pattern >> (8 * offset));
  }

  /// <summary>The number of bytes of the type, all of which have to lie in the array.</summary>
  private static int Inside(IrType type, byte[] array, long index) {
    var size = Width(type) / 8;
    if (index < 0 || index + size > array.Length)
      throw new IrTrapException(IrTrap.IndexOutOfRange);

    return size;
  }

}
