// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  private const string OutsideField = "  if (start < 0 || length < 0 || start + length > bits) " + Invalid;
  private const string OutsideField64 = "  if (start < 0 || length < 0 || start + length > 64) " + Invalid;
  private const string OutsideWidth = "  if (position < 0 || position >= bits) " + Invalid;
  private const string OutsideWidth64 = "  if (position < 0 || position >= 64) " + Invalid;

  /// <summary>
  /// Fields and single bits, each checked against the width first. Of up to 32 bits a field is
  /// shifted and masked as 32 bits, its mask made unsigned before one is subtracted so that 31 bits
  /// do not overflow; of 64 bits the <c>BigInt</c> is shifted by a <c>BigInt</c> count, and a pair
  /// by the shifts of the halves.
  /// </summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> BitFieldHelpers => [
    Same("$extract", Lines(
      "function $extract(value, start, length, bits) {",
      OutsideField,
      "  if (length === 0) return 0;",
      "  @const pattern = bits === 32 ? value >>> 0 : value & ((1 << bits) - 1);",
      "  return length === 32 ? pattern : (pattern >>> start) & (((1 << length) >>> 0) - 1);",
      "}")),
    Same("$insert", Lines(
      "function $insert(value, field, start, length, bits) {",
      OutsideField,
      "  if (length === 0) return value;",
      "  @const mask = (length === 32 ? -1 : ((1 << length) >>> 0) - 1) << start;",
      "  return (value & ~mask) | ((field << start) & mask);",
      "}")),
    Same("$testbit", Lines("function $testbit(value, position, bits) {", OutsideWidth, "  return ((value >>> position) & 1) === 1;", "}")),
    Same("$setbit", Lines("function $setbit(value, position, bits) {", OutsideWidth, "  return value | (1 << position);", "}")),
    Same("$clearbit", Lines("function $clearbit(value, position, bits) {", OutsideWidth, "  return value & ~(1 << position);", "}")),
    Same("$togglebit", Lines("function $togglebit(value, position, bits) {", OutsideWidth, "  return value ^ (1 << position);", "}")),
    Wide("$extract64",
      Lines(
        "function $extract64(value, start, length) {",
        OutsideField64,
        "  return (BigInt.asUintN(64, value) >> BigInt(start)) & ((1n << BigInt(length)) - 1n);",
        "}"),
      Lines(
        "function $extract64(value, start, length) {",
        OutsideField64,
        $"  if (length === 0) return {Zero64};",
        "  return $and64($shr64(value, start), $lowmask64(length));",
        "}")),
    Wide("$insert64",
      Lines(
        "function $insert64(value, field, start, length) {",
        OutsideField64,
        "  @const mask = ((1n << BigInt(length)) - 1n) << BigInt(start);",
        "  return (value & ~mask) | ((field << BigInt(start)) & mask);",
        "}"),
      Lines(
        "function $insert64(value, field, start, length) {",
        OutsideField64,
        "  if (length === 0) return value;",
        "  @const mask = $shl64($lowmask64(length), start);",
        "  return $or64($and64(value, $not64(mask)), $and64($shl64(field, start), mask));",
        "}")),
    Wide("$testbit64",
      Lines("function $testbit64(value, position) {", OutsideWidth64, "  return ((value >> BigInt(position)) & 1n) === 1n;", "}"),
      Lines("function $testbit64(value, position) {", OutsideWidth64, "  return ((position < 32 ? value.lo >>> position : value.hi >>> (position - 32)) & 1) === 1;", "}")),
    Wide("$setbit64",
      Lines("function $setbit64(value, position) {", OutsideWidth64, "  return value | (1n << BigInt(position));", "}"),
      Lines("function $setbit64(value, position) {", OutsideWidth64, $"  return $or64(value, $shl64({One64}, position));", "}")),
    Wide("$clearbit64",
      Lines("function $clearbit64(value, position) {", OutsideWidth64, "  return value & ~(1n << BigInt(position));", "}"),
      Lines("function $clearbit64(value, position) {", OutsideWidth64, $"  return $and64(value, $not64($shl64({One64}, position)));", "}")),
    Wide("$togglebit64",
      Lines("function $togglebit64(value, position) {", OutsideWidth64, "  return value ^ (1n << BigInt(position));", "}"),
      Lines("function $togglebit64(value, position) {", OutsideWidth64, $"  return $xor64(value, $shl64({One64}, position));", "}"))
  ];

}
