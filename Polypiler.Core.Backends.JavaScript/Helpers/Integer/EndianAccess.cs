using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  private const string OutsideBytes = "  if (index < 0 || index + size > array.length) throw new RangeError(\"index out of range\");";
  private const string OutsideBytes64 = "  if (index < 0 || index + 8 > array.length) throw new RangeError(\"index out of range\");";

  /// <summary>
  /// Loads and stores of the bytes of an integer, typed array or plain, the whole range checked
  /// before anything is read or written: up to 32 bits as a number built from the bytes, which is
  /// exact, of 64 bits as a <c>BigInt</c> or as two halves of four bytes each.
  /// </summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> EndianHelpers => [
    Same("$load", Lines(
      "function $load(array, index, size, little) {",
      OutsideBytes,
      "  @let value = 0;",
      "  for (@let k = 0; k < size; ++k) value = value * 256 + array[little ? index + size - 1 - k : index + k];",
      "  return value;",
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
      "  for (@let k = 0; k < size; ++k) array[little ? index + k : index + size - 1 - k] = (value >>> (8 * k)) & 0xFF;",
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
        "}"))
  ];

}
