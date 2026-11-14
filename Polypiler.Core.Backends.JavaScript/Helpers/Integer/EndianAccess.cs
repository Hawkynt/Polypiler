using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  private const string OutsideBytes = "  if (index < 0 || index + size > array.length) throw new RangeError(\"index out of range\");";
  private const string OutsideBytes64 = "  if (index < 0 || index + 8 > array.length) throw new RangeError(\"index out of range\");";

  /// <summary>
  /// Loads and stores of the bytes of an integer, typed array or plain, the whole range checked
  /// before anything is read or written: of 16 and 32 bits written out byte by byte, so that they cost
  /// no more than the byte accesses they stand for; of 64 bits as a <c>BigInt</c> or as two halves of
  /// four bytes each.
  /// </summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> EndianHelpers => [
    Same("$load", Lines(
      "function $load(array, index, size, little) {",
      OutsideBytes,
      "  if (size === 2) return little ? array[index] | (array[index + 1] << 8) : (array[index] << 8) | array[index + 1];",
      "  return (little",
      "    ? array[index] | (array[index + 1] << 8) | (array[index + 2] << 16) | (array[index + 3] << 24)",
      "    : (array[index] << 24) | (array[index + 1] << 16) | (array[index + 2] << 8) | array[index + 3]) >>> 0;",
      "}")),
    Wide("$load64",
      Lines(
        "function $load64(array, index, little) {",
        OutsideBytes64,
        "  @let value = 0n;",
        "  for (@let k = 0; k < 8; ++k) value = (value << 8n) | BigInt(array[little ? index + 7 - k : index + k]);",
        "  return value;",
        "}"),
      Lines(
        "function $load64(array, index, little) {",
        OutsideBytes64,
        "  @let hi = 0;",
        "  @let lo = 0;",
        "  for (@let k = 0; k < 4; ++k) {",
        "    hi = hi * 256 + array[little ? index + 7 - k : index + k];",
        "    lo = lo * 256 + array[little ? index + 3 - k : index + 4 + k];",
        "  }",
        "  return { hi: hi, lo: lo };",
        "}")),
    Same("$store", Lines(
      "function $store(array, index, value, size, little) {",
      OutsideBytes,
      "  @const last = index + size - 1;",
      "  array[little ? index : last] = value & 0xFF;",
      "  array[little ? index + 1 : last - 1] = (value >>> 8) & 0xFF;",
      "  if (size === 4) {",
      "    array[little ? index + 2 : index + 1] = (value >>> 16) & 0xFF;",
      "    array[little ? index + 3 : index] = (value >>> 24) & 0xFF;",
      "  }",
      "}")),
    Wide("$store64",
      Lines(
        "function $store64(array, index, value, little) {",
        OutsideBytes64,
        "  for (@let k = 0; k < 8; ++k) array[little ? index + k : index + 7 - k] = Number((value >> BigInt(8 * k)) & 0xFFn);",
        "}"),
      Lines(
        "function $store64(array, index, value, little) {",
        OutsideBytes64,
        "  for (@let k = 0; k < 8; ++k) array[little ? index + k : index + 7 - k] = (k < 4 ? value.lo >>> (8 * k) : value.hi >>> (8 * (k - 4))) & 0xFF;",
        "}")),
    // A floating-point number through a DataView of the bytes of the array, which only a typed array has; a view is made per access, so that the array may be any Uint8Array, also one of part of a larger buffer.
    Same("$loadf32", Float("function $loadf32(array, index, little) {", 4, "return new DataView(array.buffer, array.byteOffset, array.byteLength).getFloat32(index, little);")),
    Same("$loadf64", Float("function $loadf64(array, index, little) {", 8, "return new DataView(array.buffer, array.byteOffset, array.byteLength).getFloat64(index, little);")),
    Same("$storef32", Float("function $storef32(array, index, value, little) {", 4, "new DataView(array.buffer, array.byteOffset, array.byteLength).setFloat32(index, value, little);")),
    Same("$storef64", Float("function $storef64(array, index, value, little) {", 8, "new DataView(array.buffer, array.byteOffset, array.byteLength).setFloat64(index, value, little);"))
  ];

  /// <summary>A helper on a floating-point number in the bytes of an array: the range checked, then the access.</summary>
  private static string Float(string declaration, int size, string access)
    => Lines(declaration, $"  if (index < 0 || index + {size} > array.length) throw new RangeError(\"index out of range\");", "  " + access, "}");

}
