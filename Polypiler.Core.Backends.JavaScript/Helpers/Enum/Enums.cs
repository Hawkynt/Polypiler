// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The helpers of the operations on enums, which are .NET's rules (docs/reference/enums.md). Each takes
/// the private table of its enum, the members in .NET's order as <c>[name, low]</c> or, of 64 bits,
/// <c>[name, low, high]</c>: the bits of the value as unsigned halves. A value is a number of up to 32
/// bits, a <c>BigInt</c> or a pair of halves, which the helpers read as its unsigned halves; an enum of
/// 64 bits has helpers of its own, so that a module of narrower enums carries nothing of 64 bits. No
/// member has an empty name, so the empty string stands for a value without one.
/// </summary>
internal static partial class JavaScriptHelpers {

  /// <summary>Every helper of the enums with its text for a profile.</summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> EnumHelpers => [
    ("$enumBits", native => Lines(
      "function $enumBits(x, bits) {",
      "  if (typeof x === \"number\") return [0, bits === 32 ? x >>> 0 : x & ((1 << bits) - 1)];",
      native("BigInt")
        ? "  return [Number(BigInt.asUintN(64, x) >> 32n), Number(BigInt.asUintN(32, x))];"
        : "  return [x.hi, x.lo];",
      "}")),
    Same("$enumFlags", Lines(
      "function $enumFlags(table, flags, high, low) {",
      "  for (@let k = 0; k < table.length; ++k) if (table[k][1] === low && (table[k].length > 2 ? table[k][2] : 0) === high) return table[k][0];",
      "  if (!flags || (high === 0 && low === 0)) return \"\";",
      "  @let text = \"\";",
      "  for (@let k = table.length - 1; k >= 0 && (high !== 0 || low !== 0); --k) {",
      "    @const fh = table[k].length > 2 ? table[k][2] : 0;",
      "    @const fl = table[k][1];",
      "    if ((fh !== 0 || fl !== 0) && ((high & fh) >>> 0) === fh && ((low & fl) >>> 0) === fl) {",
      "      high = (high & ~fh) >>> 0;",
      "      low = (low & ~fl) >>> 0;",
      "      text = text === \"\" ? table[k][0] : table[k][0] + \", \" + text;",
      "    }",
      "  }",
      "  return high === 0 && low === 0 ? text : \"\";",
      "}")),
    Same("$enumName", Lines(
      "function $enumName(table, flags, x, bits) {",
      "  @const v = $enumBits(x, bits);",
      "  @const name = $enumFlags(table, flags, v[0], v[1]);",
      "  return name === \"\" ? String(x) : name;",
      "}")),
    Same("$enumName64", Lines(
      "function $enumName64(table, flags, x, signed) {",
      "  @const name = $enumFlags(table, flags, x.hi, x.lo);",
      "  return name === \"\" ? $radix64(x, 10, 0, false, signed) : name;",
      "}")),
    Same("$enumDefined", Lines(
      "function $enumDefined(table, x, bits) {",
      "  @const v = $enumBits(x, bits);",
      "  for (@let k = 0; k < table.length; ++k) if (table[k][1] === v[1] && (table[k].length > 2 ? table[k][2] : 0) === v[0]) return true;",
      "  return false;",
      "}")),
    Same("$enumNumber", Lines(
      "function $enumNumber(t) {",
      "  if (t === \"\") throw new ArgumentError(\"unknown name\");",
      "  @const c = t.charAt(0);",
      "  if ((c < \"0\" || c > \"9\") && c !== \"-\" && c !== \"+\") return false;",
      "  @let end = t.length;",
      "  while (end > 0 && t.charAt(end - 1) === \"\\u0000\") --end;",
      "  while (end > 0 && \"\\u0009\\n\\u000B\\u000C\\u000D \".indexOf(t.charAt(end - 1)) >= 0) --end;",
      "  @const at = c === \"-\" || c === \"+\" ? 1 : 0;",
      "  if (at >= end) return false;",
      "  for (@let k = at; k < end; ++k) if (t.charAt(k) < \"0\" || t.charAt(k) > \"9\") return false;",
      "  return true;",
      "}")),
    Same("$enumSame", Lines(
      "function $enumSame(a, b, ignoreCase) {",
      "  if (!ignoreCase || a.length !== b.length) return a === b;",
      "  for (@let k = 0; k < a.length; ++k) {",
      "    @const x = a.charAt(k);",
      "    @const y = b.charAt(k);",
      "    @const upperX = x.toUpperCase();",
      "    @const upperY = y.toUpperCase();",
      "    if (x !== y && (upperX.length === 1 ? upperX : x) !== (upperY.length === 1 ? upperY : y)) return false;",
      "  }",
      "  return true;",
      "}")),
    Same("$enumLookup", Lines(
      "function $enumLookup(table, t, ignoreCase) {",
      "  @const parts = t.split(\",\");",
      "  @let high = 0;",
      "  @let low = 0;",
      "  for (@let k = 0; k < parts.length; ++k) {",
      "    @const name = $trimNet(parts[k], true, true);",
      "    @let found = -1;",
      "    for (@let j = 0; j < table.length && found < 0; ++j) if ($enumSame(table[j][0], name, ignoreCase)) found = j;",
      "    if (found < 0) throw new ArgumentError(\"unknown name\");",
      "    low = (low | table[found][1]) >>> 0;",
      "    high = (high | (table[found].length > 2 ? table[found][2] : 0)) >>> 0;",
      "  }",
      "  return [high, low];",
      "}")),
    Same("$enumParse", Lines(
      "function $enumParse(table, s, ignoreCase, bits, signed) {",
      "  @const t = $trimNet(s, true, false);",
      "  if ($enumNumber(t)) return $parseNetInt(t, signed ? -Math.pow(2, bits - 1) : 0, signed ? Math.pow(2, bits - 1) - 1 : Math.pow(2, bits) - 1);",
      "  @const low = $enumLookup(table, t, ignoreCase)[1];",
      "  if (bits === 32) return signed ? low | 0 : low;",
      "  return signed ? (low << (32 - bits)) >> (32 - bits) : low;",
      "}")),
    ("$enumParse64", native => Lines(
      "function $enumParse64(table, s, ignoreCase, signed) {",
      "  @const t = $trimNet(s, true, false);",
      "  if ($enumNumber(t)) return $parseNet64(t, signed);",
      "  @const v = $enumLookup(table, t, ignoreCase);",
      native("BigInt")
        ? "  return signed ? BigInt.asIntN(64, (BigInt(v[0]) << 32n) | BigInt(v[1])) : (BigInt(v[0]) << 32n) | BigInt(v[1]);"
        : "  return { hi: v[0], lo: v[1] };",
      "}"))
  ];

  /// <summary>The names of the helpers of the enums, in the order a module lists them, after those of the conversions.</summary>
  private static IEnumerable<string> EnumOrder => EnumHelpers.Select(helper => helper.Name);

  private static readonly Dictionary<string, Func<Func<string, bool>, string>> _enum = EnumHelpers.ToDictionary(helper => helper.Name, helper => helper.Text);

  /// <summary>The text of a helper of the enums for the profile, or <see langword="null"/> for a name that is none.</summary>
  private static string? EnumSource(string name, Func<string, bool> native) => _enum.TryGetValue(name, out var text) ? text(native) : null;

}
