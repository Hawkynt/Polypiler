// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  private const string NullPointer = "if (r === null) throw new TypeError(\"null reference\");";

  /// <summary>
  /// The helpers of the bounded pointers (#1331). JavaScript has no pointers: a pointer is a region and a byte
  /// offset in it. A region is an object <c>{ a, o, n, s, k }</c>: the array its elements are in - a typed array,
  /// or a plain one where the profile stores the type so - the index of its first element, the number of elements,
  /// their size in bytes and the name of their kind as a <c>DataView</c> spells it (<c>"Uint8"</c>, <c>"Float64"</c>,
  /// <c>"BigInt64"</c>, …). The null pointer is the region <c>null</c>. A load or a store checks that all its bytes lie
  /// in the region, reads an element of the region's own kind directly and any other through the bytes, lowest
  /// first: a <c>DataView</c> over a typed array, the bits of the elements of a plain one.
  /// </summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> PointerHelpers => [
    Same("$pin", Lines(
      "function $pin(a, s, k) {",
      "  if (a === null || a.length === 0) return null;",
      "  return { a: a, o: 0, n: a.length, s: s, k: k };",
      "}")),
    Same("$pinv", Lines(
      "function $pinv(v, s, k) {",
      "  if (v.n === 0) return null;",
      "  return { a: v.a, o: v.o, n: v.n, s: s, k: k };",
      "}")),
    Same("$pin0", Lines(
      "function $pin0(a, s, k) {",
      "  return { a: a, o: 0, n: a.length, s: s, k: k };",
      "}")),
    Same("$pinat", Lines(
      "function $pinat(a, i, s, k) {",
      "  if (a === null) throw new TypeError(\"null reference\");",
      "  if (i < 0 || i >= a.length) " + IndexOutOfRange,
      "  return { a: a, o: 0, n: a.length, s: s, k: k, i: i * s };",
      "}")),
    Same("$pinatv", Lines(
      "function $pinatv(v, i, s, k) {",
      "  if (i < 0 || i >= v.n) " + IndexOutOfRange,
      "  return { a: v.a, o: v.o, n: v.n, s: s, k: k, i: i * s };",
      "}")),
    Same("$pat", Lines(
      "function $pat(r, at, size) {",
      "  " + NullPointer,
      "  if (at < 0 || at > r.n * r.s - size) " + IndexOutOfRange,
      "}")),
    Same("$pget", Lines(
      "function $pget(r, at, size, kind) {",
      "  $pat(r, at, size);",
      "  if (r.k === kind && at % size === 0) return r.a[r.o + at / size];",
      "  return $pread(r, at, size, kind);",
      "}")),
    Same("$pset", Lines(
      "function $pset(r, at, size, kind, value) {",
      "  $pat(r, at, size);",
      "  if (r.k === kind && at % size === 0) r.a[r.o + at / size] = value;",
      "  else $pwrite(r, at, size, kind, value);",
      "}")),
    Same("$pdv", Lines(
      "function $pdv(r, at, size) {",
      "  $pat(r, at, size);",
      "  return new DataView(r.a.buffer, r.a.byteOffset + r.o * r.s + at, size);",
      "}")),
    ("$pread", native => native("BigInt64Array")
      ? Lines(
        "function $pread(r, at, size, kind) {",
        "  return new DataView(r.a.buffer, r.a.byteOffset + r.o * r.s + at, size)[\"get\" + kind](0, true);",
        "}")
      : native("DataView")
        ? Lines(
          "function $pread(r, at, size, kind) {",
          "  if (r.a.buffer === void 0) return $preadb(r, at, size, kind);",
          "  @const d = new DataView(r.a.buffer, r.a.byteOffset + r.o * r.s + at, size);",
          "  if (size === 8 && kind !== \"Float64\") return { hi: d.getUint32(4, true), lo: d.getUint32(0, true) };",
          "  return d[\"get\" + kind](0, true);",
          "}")
        : Lines("function $pread(r, at, size, kind) {", "  return $preadb(r, at, size, kind);", "}")),
    ("$pwrite", native => native("BigInt64Array")
      ? Lines(
        "function $pwrite(r, at, size, kind, value) {",
        "  new DataView(r.a.buffer, r.a.byteOffset + r.o * r.s + at, size)[\"set\" + kind](0, value, true);",
        "}")
      : native("DataView")
        ? Lines(
          "function $pwrite(r, at, size, kind, value) {",
          "  if (r.a.buffer === void 0) {",
          "    $pwriteb(r, at, size, kind, value);",
          "    return;",
          "  }",
          "  @const d = new DataView(r.a.buffer, r.a.byteOffset + r.o * r.s + at, size);",
          "  if (size === 8 && kind !== \"Float64\") {",
          "    d.setUint32(0, value.lo, true);",
          "    d.setUint32(4, value.hi, true);",
          "  } else {",
          "    d[\"set\" + kind](0, value, true);",
          "  }",
          "}")
        : Lines("function $pwrite(r, at, size, kind, value) {", "  $pwriteb(r, at, size, kind, value);", "}")),
    // The bytes of an element of a plain array, lowest first, and the element of bytes: a 64-bit integer is its halves.
    Same("$pbytes", Lines(
      "function $pbytes(e, size, kind) {",
      "  if (kind === \"Float32\") e = $bits32(e);",
      "  else if (kind === \"Float64\") e = $bits64(e);",
      "  @const out = [];",
      "  for (@let b = 0; b < size; ++b) out[b] = size === 8 ? ((b < 4 ? e.lo : e.hi) >>> (8 * (b % 4))) & 255 : (e >>> (8 * b)) & 255;",
      "  return out;",
      "}")),
    Same("$pvalue", Lines(
      "function $pvalue(bytes, size, kind) {",
      "  if (size === 8) {",
      "    @const v = { hi: ((bytes[7] << 24) | (bytes[6] << 16) | (bytes[5] << 8) | bytes[4]) >>> 0, lo: ((bytes[3] << 24) | (bytes[2] << 16) | (bytes[1] << 8) | bytes[0]) >>> 0 };",
      "    return kind === \"Float64\" ? $float64(v) : v;",
      "  }",
      "  @let x = 0;",
      "  for (@let b = size - 1; b >= 0; --b) x = x * 256 + bytes[b];",
      "  if (kind === \"Int8\") return (x << 24) >> 24;",
      "  if (kind === \"Int16\") return (x << 16) >> 16;",
      "  if (kind === \"Int32\") return x | 0;",
      "  if (kind === \"Float32\") return $float32(x | 0);",
      "  return x;",
      "}")),
    Same("$preadb", Lines(
      "function $preadb(r, at, size, kind) {",
      "  @const out = [];",
      "  for (@let b = 0; b < size; ++b) {",
      "    @const m = Math.floor((at + b) / r.s);",
      "    out[b] = $pbytes(r.a[r.o + m], r.s, r.k)[at + b - m * r.s];",
      "  }",
      "  return $pvalue(out, size, kind);",
      "}")),
    Same("$pwriteb", Lines(
      "function $pwriteb(r, at, size, kind, value) {",
      "  @const bytes = $pbytes(value, size, kind);",
      "  for (@let b = 0; b < size; ++b) {",
      "    @const m = Math.floor((at + b) / r.s);",
      "    @const e = $pbytes(r.a[r.o + m], r.s, r.k);",
      "    e[at + b - m * r.s] = bytes[b];",
      "    r.a[r.o + m] = $pvalue(e, r.s, r.k);",
      "  }",
      "}")),
    // Two pointers are in one region when their arrays share their memory; the byte of the array memory they are at orders them.
    Same("$pbase", Lines(
      "function $pbase(r) {",
      "  return r === null ? null : r.a.buffer !== void 0 ? r.a.buffer : r.a;",
      "}")),
    Same("$ppos", Lines(
      "function $ppos(r, at) {",
      "  return r === null ? at : (r.a.buffer !== void 0 ? r.a.byteOffset : 0) + r.o * r.s + at;",
      "}")),
    Same("$peq", Lines(
      "function $peq(r1, o1, r2, o2) {",
      "  return $pbase(r1) === $pbase(r2) && $ppos(r1, o1) === $ppos(r2, o2);",
      "}")),
    Same("$pcmp", Lines(
      "function $pcmp(r1, o1, r2, o2) {",
      "  if ($pbase(r1) !== $pbase(r2)) throw new ArithmeticError(\"invalid operation\");",
      "  return $ppos(r1, o1) - $ppos(r2, o2);",
      "}"))
  ];

  /// <summary>The names of the helpers of the pointers, in the order a module lists them, after the helpers of the views.</summary>
  private static IEnumerable<string> PointerOrder => PointerHelpers.Select(helper => helper.Name);

  private static readonly Dictionary<string, Func<Func<string, bool>, string>> _pointers = PointerHelpers.ToDictionary(helper => helper.Name, helper => helper.Text);

  /// <summary>The text of a helper of the pointers, or <see langword="null"/> for a name that is none.</summary>
  private static string? PointerSource(string name, Func<string, bool> native) => _pointers.TryGetValue(name, out var text) ? text(native) : null;

}
