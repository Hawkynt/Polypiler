// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  private const string OutsideCount = "  if (count < 0 || count > bits) " + Invalid;
  private const string OutsideCount64 = "  if (count < 0 || count > 64) " + Invalid;
  private const string OutsideExtension = "  if (bits < 1 || bits > width) " + Invalid;
  private const string OutsideExtension64 = "  if (bits < 1 || bits > 64) " + Invalid;

  /// <summary>
  /// Masks of the lowest bits, the forms of the lowest set bit (Warren, <i>Hacker's Delight</i>, 2nd
  /// ed., §2-1) on the value read once, and the extensions of a field at the bottom: by two shifts of
  /// 32 bits, by <c>BigInt.asIntN</c> and <c>BigInt.asUintN</c>, or by the shifts of a pair.
  /// </summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> MaskHelpers => [
    Same("$lowmask", Lines("function $lowmask(count, bits) {", OutsideCount, "  return count === 32 ? 4294967295 : ((1 << count) >>> 0) - 1;", "}")),
    Wide("$lowmask64",
      Lines("function $lowmask64(count) {", OutsideCount64, "  return (1n << BigInt(count)) - 1n;", "}"),
      Lines(
        "function $lowmask64(count) {",
        OutsideCount64,
        $"  if (count === 64) return {AllOnes64};",
        "  return count >= 32 ? { hi: ((1 << (count - 32)) >>> 0) - 1, lo: 4294967295 } : { hi: 0, lo: ((1 << count) >>> 0) - 1 };",
        "}")),
    Same("$blsi", Lines("function $blsi(value) {", "  return value & -value;", "}")),
    Same("$blsr", Lines("function $blsr(value) {", "  return value & (value - 1);", "}")),
    Same("$blsmsk", Lines("function $blsmsk(value) {", "  return value ^ (value - 1);", "}")),
    Wide("$blsi64",
      Lines("function $blsi64(value) {", "  return value & -value;", "}"),
      Lines("function $blsi64(value) {", "  return $and64(value, $neg64(value));", "}")),
    Wide("$blsr64",
      Lines("function $blsr64(value) {", "  return value & (value - 1n);", "}"),
      Lines("function $blsr64(value) {", $"  return $and64(value, $sub64(value, {One64}));", "}")),
    Wide("$blsmsk64",
      Lines("function $blsmsk64(value) {", "  return value ^ (value - 1n);", "}"),
      Lines("function $blsmsk64(value) {", $"  return $xor64(value, $sub64(value, {One64}));", "}")),
    Same("$sext", Lines("function $sext(value, bits, width) {", OutsideExtension, "  return (value << (32 - bits)) >> (32 - bits);", "}")),
    Same("$zext", Lines("function $zext(value, bits, width) {", OutsideExtension, "  return bits === 32 ? value >>> 0 : value & (((1 << bits) >>> 0) - 1);", "}")),
    Wide("$sext64",
      Lines("function $sext64(value, bits) {", OutsideExtension64, "  return BigInt.asIntN(bits, value);", "}"),
      Lines("function $sext64(value, bits) {", OutsideExtension64, "  return $sar64($shl64(value, 64 - bits), 64 - bits);", "}")),
    Wide("$zext64",
      Lines("function $zext64(value, bits) {", OutsideExtension64, "  return BigInt.asUintN(bits, value);", "}"),
      Lines("function $zext64(value, bits) {", OutsideExtension64, "  return $and64(value, $lowmask64(bits));", "}"))
  ];

}
