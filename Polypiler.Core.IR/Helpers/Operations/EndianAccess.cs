// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.IR;

/// <summary>
/// Integers read from and written to an array of bytes, the lowest byte first or the highest byte
/// first. Every byte of the access has to lie within the array; otherwise the access traps before it
/// reads or writes any.
/// </summary>
public static partial class HelperOperations {

  /// <summary>The value of 2 or 4 bytes of an array from an index on.</summary>
  /// <param name="array">The bytes.</param>
  /// <param name="index">The index of the first byte.</param>
  /// <param name="size">The number of bytes, 2 or 4.</param>
  /// <param name="little">Whether the lowest byte comes first.</param>
  /// <exception cref="IndexOutOfRangeException">A byte lies outside the array.</exception>
  public static uint Load32(byte[] array, int index, int size, bool little) {
    CheckBytes(array, index, size);
    if (size == 2)
      return little ? (uint)(array[index] | (array[index + 1] << 8)) : (uint)((array[index] << 8) | array[index + 1]);

    return little
      ? (uint)(array[index] | (array[index + 1] << 8) | (array[index + 2] << 16) | (array[index + 3] << 24))
      : (uint)((array[index] << 24) | (array[index + 1] << 16) | (array[index + 2] << 8) | array[index + 3]);
  }

  /// <summary>The value of 8 bytes of an array from an index on.</summary>
  /// <param name="array">The bytes.</param>
  /// <param name="index">The index of the first byte.</param>
  /// <param name="little">Whether the lowest byte comes first.</param>
  /// <exception cref="IndexOutOfRangeException">A byte lies outside the array.</exception>
  public static ulong Load64(byte[] array, int index, bool little) {
    CheckBytes(array, index, 8);
    var value = 0UL;
    for (var offset = 0; offset < 8; ++offset)
      value = (value << 8) | array[index + (little ? 7 - offset : offset)];

    return value;
  }

  /// <summary>Writes the lowest 2 or 4 bytes of a value into an array from an index on.</summary>
  /// <param name="array">The bytes.</param>
  /// <param name="index">The index of the first byte.</param>
  /// <param name="value">The value.</param>
  /// <param name="size">The number of bytes, 2 or 4.</param>
  /// <param name="little">Whether the lowest byte comes first.</param>
  /// <exception cref="IndexOutOfRangeException">A byte lies outside the array.</exception>
  public static void Store32(byte[] array, int index, uint value, int size, bool little) {
    CheckBytes(array, index, size);
    for (var offset = 0; offset < size; ++offset)
      array[index + (little ? offset : size - 1 - offset)] = (byte)(value >> (8 * offset));
  }

  /// <summary>Writes the 8 bytes of a value into an array from an index on.</summary>
  /// <param name="array">The bytes.</param>
  /// <param name="index">The index of the first byte.</param>
  /// <param name="value">The value.</param>
  /// <param name="little">Whether the lowest byte comes first.</param>
  /// <exception cref="IndexOutOfRangeException">A byte lies outside the array.</exception>
  public static void Store64(byte[] array, int index, ulong value, bool little) {
    CheckBytes(array, index, 8);
    for (var offset = 0; offset < 8; ++offset)
      array[index + (little ? offset : 7 - offset)] = (byte)(value >> (8 * offset));
  }

  private static void CheckBytes(byte[] array, int index, int size) {
    if (index < 0 || index > array.Length - size)
      throw new IndexOutOfRangeException("index out of range");
  }

}
