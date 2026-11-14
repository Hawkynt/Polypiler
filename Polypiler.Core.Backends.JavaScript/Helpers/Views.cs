// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  private const string ArgumentOutOfRange = "throw new RangeError(\"argument out of range\");";
  private const string IndexOutOfRange = "throw new RangeError(\"index out of range\");";

  /// <summary>
  /// The helpers of the views (#1325). Where the profile has typed arrays a view is the
  /// <c>subarray</c> of one, which shares its memory, and <c>$slice</c> checks the range that
  /// <c>subarray</c> would clamp silently. Elsewhere a view is an object <c>{ a, o, n }</c>: the plain
  /// array, the index of the first element and the number of elements, and the helpers ending in
  /// <c>o</c> work on it. A range outside its source throws the <c>RangeError</c> of the bounds of an
  /// argument, an element outside its view that of an index.
  /// </summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> ViewHelpers => [
    Same("$slice", Lines(
      "function $slice(view, start, length) {",
      "  if (length === void 0) length = view.length - start;",
      "  if (start < 0 || length < 0 || start > view.length || length > view.length - start) " + ArgumentOutOfRange,
      "  return view.subarray(start, start + length);",
      "}")),
    Same("$vobj", Lines(
      "function $vobj(array, start, length) {",
      "  if (start === void 0) start = 0;",
      "  if (length === void 0) length = array.length - start;",
      "  if (start < 0 || length < 0 || start > array.length || length > array.length - start) " + ArgumentOutOfRange,
      "  return { a: array, o: start, n: length };",
      "}")),
    Same("$vsub", Lines(
      "function $vsub(view, start, length) {",
      "  if (length === void 0) length = view.n - start;",
      "  if (start < 0 || length < 0 || start > view.n || length > view.n - start) " + ArgumentOutOfRange,
      "  return { a: view.a, o: view.o + start, n: length };",
      "}")),
    Same("$vix", Lines(
      "function $vix(view, index) {",
      "  if (index < 0 || index >= view.n) " + IndexOutOfRange,
      "  return view.o + index;",
      "}")),
    Same("$vget", Lines("function $vget(view, index) {", "  return view.a[$vix(view, index)];", "}")),
    Same("$vset", Lines("function $vset(view, index, value) {", "  view.a[$vix(view, index)] = value;", "}")),
    Same("$vat", Lines(
      "function $vat(view, index, size) {",
      "  @const length = view.n === void 0 ? view.length : view.n;",
      "  if (index < 0 || index > length - size) " + ArgumentOutOfRange,
      "  return view.n === void 0 ? index : view.o + index;",
      "}")),
    Same("$vdv", Lines(
      "function $vdv(view, index, size) {",
      "  if (index < 0 || index > view.length - size) " + ArgumentOutOfRange,
      "  return new DataView(view.buffer, view.byteOffset + index, size);",
      "}")),
    Same("$vstore", Lines(
      "function $vstore(view, index, size, value, little, method) {",
      "  $vdv(view, index, size)[method](0, value, little);",
      "}")),
    Same("$vcopy", Lines(
      "function $vcopy(source, target) {",
      "  if (source.length > target.length) " + ArgumentOutOfRange,
      "  target.set(source);",
      "}")),
    Same("$vtrycopy", Lines(
      "function $vtrycopy(source, target) {",
      "  if (source.length > target.length) return false;",
      "  target.set(source);",
      "  return true;",
      "}")),
    Same("$vcopyo", Lines(
      "function $vcopyo(source, target) {",
      "  if (source.n > target.n) " + ArgumentOutOfRange,
      "  if (source.a === target.a && source.o < target.o) {",
      "    for (@let k = source.n - 1; k >= 0; --k) target.a[target.o + k] = source.a[source.o + k];",
      "  } else {",
      "    for (@let k = 0; k < source.n; ++k) target.a[target.o + k] = source.a[source.o + k];",
      "  }",
      "}")),
    Same("$vtrycopyo", Lines(
      "function $vtrycopyo(source, target) {",
      "  if (source.n > target.n) return false;",
      "  $vcopyo(source, target);",
      "  return true;",
      "}")),
    Same("$vfillo", Lines(
      "function $vfillo(view, value) {",
      "  for (@let k = 0; k < view.n; ++k) view.a[view.o + k] = value;",
      "}")),
    // Equality as .NET's: a NaN equals a NaN, and === already takes 0 and -0 as equal.
    Same("$vequal", Lines(
      "function $vequal(a, b) {",
      "  if (a.length !== b.length) return false;",
      "  for (@let k = 0; k < a.length; ++k) {",
      "    @const x = a[k];",
      "    @const y = b[k];",
      "    if (!(x === y || (x !== x && y !== y))) return false;",
      "  }",
      "  return true;",
      "}")),
    Same("$vequalo", Lines(
      "function $vequalo(a, b) {",
      "  if (a.n !== b.n) return false;",
      "  for (@let k = 0; k < a.n; ++k) {",
      "    @const x = a.a[a.o + k];",
      "    @const y = b.a[b.o + k];",
      "    if (typeof x === \"object\" ? x.hi !== y.hi || x.lo !== y.lo : !(x === y || (x !== x && y !== y))) return false;",
      "  }",
      "  return true;",
      "}")),
    Same("$vtoarray", Lines("function $vtoarray(view) {", "  return new view.constructor(view);", "}")),
    Same("$vtoarrayo", Lines(
      "function $vtoarrayo(view) {",
      "  @const copy = [];",
      "  for (@let k = 0; k < view.n; ++k) copy[k] = view.a[view.o + k];",
      "  return copy;",
      "}")),
    // A reinterpretation copies: the bytes of every element, the lowest first, read back as elements of the other type.
    Same("$vcast", Lines(
      "function $vcast(view, size, set, unit, get, made) {",
      "  @const bytes = new DataView(new ArrayBuffer(view.length * size));",
      "  for (@let k = 0; k < view.length; ++k) bytes[set](k * size, view[k], true);",
      "  @const target = new made(Math.floor(view.length * size / unit));",
      "  for (@let k = 0; k < target.length; ++k) target[k] = bytes[get](k * unit, true);",
      "  return target;",
      "}")),
    Same("$vcasto", Lines(
      "function $vcasto(view, size, unit, signed) {",
      "  @const bytes = [];",
      "  for (@let k = 0; k < view.n; ++k) {",
      "    for (@let b = 0; b < size; ++b) bytes[k * size + b] = (view.a[view.o + k] >>> (8 * b)) & 0xFF;",
      "  }",
      "  @const target = [];",
      "  @const count = Math.floor(bytes.length / unit);",
      "  for (@let k = 0; k < count; ++k) {",
      "    @let value = 0;",
      "    for (@let b = unit - 1; b >= 0; --b) value = value * 256 + bytes[k * unit + b];",
      "    if (signed && unit < 4) value = (value << (32 - 8 * unit)) >> (32 - 8 * unit);",
      "    else if (signed) value = value | 0;",
      "    target[k] = value;",
      "  }",
      "  return { a: target, o: 0, n: count };",
      "}"))
  ];

  /// <summary>The names of the helpers of the views, in the order a module lists them, after the integer helpers.</summary>
  private static IEnumerable<string> ViewOrder => ViewHelpers.Select(helper => helper.Name);

  private static readonly Dictionary<string, Func<Func<string, bool>, string>> _views = ViewHelpers.ToDictionary(helper => helper.Name, helper => helper.Text);

  /// <summary>The text of a helper of the views, or <see langword="null"/> for a name that is none.</summary>
  private static string? ViewSource(string name, Func<string, bool> native) => _views.TryGetValue(name, out var text) ? text(native) : null;

}
