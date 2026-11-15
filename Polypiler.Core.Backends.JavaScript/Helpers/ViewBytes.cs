// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  private const string OutsideBytes = "  if (index < 0 || index + size > array.length) throw new IndexOutOfRangeError(\"index out of range\");";
  private const string OutsideBytes64 = "  if (index < 0 || index + 8 > array.length) throw new IndexOutOfRangeError(\"index out of range\");";

  /// <summary>
  /// The bytes of a view of a plain array, <c>{ a, o, n }</c>, read and written by the index into the
  /// array the view stands on: what the view's own form of an endian access calls. An endian access of
  /// an array of bytes is the library's (<c>Load32</c>, <c>Store64</c>, …); these stay with the views,
  /// whose representation the IR does not see.
  /// </summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> ViewByteHelpers => [
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
        "}"))
  ];

}
