using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>
  /// Integers of 16, 32 and 64 bits and IEEE 754 binary32 and binary64 numbers read from and written
  /// to an array of bytes at an index, in either byte order: <c>(array, index)</c> for a load, whose
  /// type is the type of the node, and <c>(array, index, value)</c> for a store, which yields nothing.
  /// A number is its encoding, read and written as the integer pattern of its width.
  /// </summary>
  private static IEnumerable<IntegerOperation> EndianAccess => [
    Loads(IrIntrinsicOp.LoadLittle, true),
    Loads(IrIntrinsicOp.LoadBig, false),
    Stores(IrIntrinsicOp.StoreLittle, true),
    Stores(IrIntrinsicOp.StoreBig, false)
  ];

  /// <summary>The types an array of bytes is read and written as: the integers of more than one byte and both floating-point formats.</summary>
  private static IrType[] Accessed => [.. _multibyte, IrType.F32, IrType.F64];

  private static IntegerOperation Loads(IrIntrinsicOp op, bool little)
    => new(op, [.. Accessed.Select(type => (type, (IReadOnlyList<IrType>)[_bytes, IrType.I32]))],
      (result, _, arguments) => Load(result, (byte[])arguments[0], Long(arguments, 1), little), false);

  private static IntegerOperation Stores(IrIntrinsicOp op, bool little)
    => new(op, [.. Accessed.Select(type => (IrType.Void, (IReadOnlyList<IrType>)[_bytes, IrType.I32, type]))],
      (_, types, arguments) => {
        Store(types[2], (byte[])arguments[0], Long(arguments, 1), arguments[2], little);
        return null;
      }, false);

  /// <summary>The value of the type whose bytes stand in the array from the index on, lowest first or highest first.</summary>
  /// <param name="type">An integer type of 16, 32 or 64 bits, <c>f32</c> or <c>f64</c>.</param>
  /// <param name="array">The bytes.</param>
  /// <param name="index">The position of the first byte.</param>
  /// <param name="little">Whether the lowest byte comes first; otherwise the highest does.</param>
  /// <returns>
  /// The value as the interpreter carries it: a <see cref="long"/> for an integer; for a number the
  /// <see cref="float"/> or <see cref="double"/> with the bits read, never converted through another format.
  /// </returns>
  /// <exception cref="IrTrapException">A byte of the value lies outside the array: an index out of range.</exception>
  public static object Load(IrType type, byte[] array, long index, bool little) {
    var size = Inside(type, array, index);
    var pattern = 0UL;
    for (var offset = 0; offset < size; ++offset)
      pattern = (pattern << 8) | array[index + (little ? size - 1 - offset : offset)];

    // The arms are of different types; without the first cast the compiler would make them all doubles.
    return type.Kind switch {
      IrTypeKind.F32 => (object)BitConverter.Int32BitsToSingle((int)pattern),
      IrTypeKind.F64 => BitConverter.Int64BitsToDouble((long)pattern),
      _ => Value(type, pattern)
    };
  }

  /// <summary>Writes the bytes of a value into the array from the index on, lowest first or highest first.</summary>
  /// <param name="type">An integer type of 16, 32 or 64 bits, <c>f32</c> or <c>f64</c>.</param>
  /// <param name="array">The bytes.</param>
  /// <param name="index">The position of the first byte.</param>
  /// <param name="value">The value as the interpreter carries it: a <see cref="long"/>, a <see cref="float"/> or a <see cref="double"/>.</param>
  /// <param name="little">Whether the lowest byte comes first; otherwise the highest does.</param>
  /// <exception cref="IrTrapException">A byte of the value lies outside the array: an index out of range, before anything is written.</exception>
  public static void Store(IrType type, byte[] array, long index, object value, bool little) {
    var size = Inside(type, array, index);
    var pattern = value switch {
      float single => (uint)BitConverter.SingleToInt32Bits(single),
      double number => (ulong)BitConverter.DoubleToInt64Bits(number),
      _ => Pattern(type, (long)value)
    };
    for (var offset = 0; offset < size; ++offset)
      array[index + (little ? offset : size - 1 - offset)] = (byte)(pattern >> (8 * offset));
  }

  /// <summary>The number of bytes of the type, all of which have to lie in the array.</summary>
  private static int Inside(IrType type, byte[] array, long index) {
    var size = type.Size;
    if (index < 0 || index + size > array.Length)
      throw new IrTrapException(IrTrap.IndexOutOfRange);

    return size;
  }

}
