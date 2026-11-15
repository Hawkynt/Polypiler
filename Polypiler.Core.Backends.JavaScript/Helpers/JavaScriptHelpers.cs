// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core.Backends.JavaScript.Syntax;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The helper functions of generated code: the operations that can trap or need more than an
/// expression, and the replacements for what a language level lacks. A helper is written once, in a
/// notation that leaves open what differs between levels: <c>@let</c> and <c>@const</c> stand for
/// the keyword that declares a variable, <c>@imul(</c>, <c>@fround(</c> and
/// <c>@trunc(</c> for the function of <c>Math</c> or the helper that replaces it. Where a level
/// changes the whole body — 64-bit integers as <c>BigInt</c>s or as pairs of halves, the bits of a
/// floating-point number through a <c>DataView</c> or by arithmetic — a helper has one body per case.
/// </summary>
internal static partial class JavaScriptHelpers {

  private const string Overflow = "throw new OverflowError(\"integer overflow\");";
  private const string ZeroDivisor64 = "  if (b === 0n) throw new DivideByZeroError(\"divide by zero\");";
  private const string Unrepresentable64 = "  if (a === -9223372036854775808n && b === -1n) " + Overflow;
  private const string OutOfRange = "  if (index < 0 || index >= array.length) throw new IndexOutOfRangeError(\"index out of range\");";
  private const string RangeOutOfRange = "  if (from < 0 || to < 0 || length < 0 || from + length > source.length || to + length > target.length) throw new IndexOutOfRangeError(\"index out of range\");";

  /// <summary>The folder of the repository every helper's source lies in, all of it released for outputs under <see cref="EmittedHelper.OwnLicence"/>.</summary>
  public const string Folder = "Polypiler.Core.Backends.JavaScript/Helpers";

  /// <summary>The names of all helpers, in the order a module lists the ones it uses.</summary>
  public static readonly IReadOnlyList<string> Order = [
    .. ErrorOrder,
    "$imul", "$f32a", "$fround", "$trunc",
    "$div64", "$rem64", "$udiv64", "$urem64",
    "$f2l", "$f2lc", "$f32",
    "$get", "$set", "$new", "$fill",
    "$view", "$bits64", "$bits32", "$float64", "$float32", "$key64",
    "$copy", "$copya", "$step", "$stepi", "$stepf", "$stepck", "$len",
    .. ViewOrder,
    .. PointerOrder,
    .. StringOrder,
    .. OptionalOrder,
    .. GenericOrder,
    .. ExceptionOrder,
    .. CollectionOrder,
    .. NumberTextOrder,
    .. EnumOrder,
    .. SequenceOrder
  ];

  private static readonly HashSet<string> _names = [.. Order];

  /// <summary>What a helper leaves open for the level to decide.</summary>
  private static readonly Regex _placeholder = new(@"@let|@const|@imul\(|@fround\(|@trunc\(", RegexOptions.CultureInvariant);

  /// <summary>Every helper as read into the syntax tree, by its text for a profile: read once, shared by every module.</summary>
  private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<JsStatement>> _read = new(StringComparer.Ordinal);

  /// <summary>
  /// The helpers a module carries: the ones its code uses and the ones those call, each written for
  /// the profile and read into the syntax tree.
  /// </summary>
  /// <param name="used">The names of the helpers the code of the module calls.</param>
  /// <param name="native">Whether the profile has a capability; asking for one it lacks and cannot replace reports it.</param>
  /// <returns>The helpers in their order, each with its statements.</returns>
  public static IReadOnlyList<(string Name, IReadOnlyList<JsStatement> Statements)> For(IEnumerable<string> used, Func<string, bool> native) {
    var written = new Dictionary<string, IReadOnlyList<JsStatement>>();
    var pending = new Queue<string>(used);
    while (pending.Count > 0) {
      var name = pending.Dequeue();
      if (written.ContainsKey(name))
        continue;

      var text = string.Join('\n', Source(name, native).ReplaceLineEndings("\n").Split('\n').Select(line => Resolve(line, native)));
      var statements = _read.GetOrAdd(text, JavaScriptSyntaxReader.Statements);
      written.Add(name, statements);
      var nodes = statements.SelectMany(JavaScriptSyntaxRewriter.DescendantsAndSelf).ToList();
      // A class extends another by its name, which is no identifier of the tree.
      foreach (var called in nodes.OfType<JsIdentifier>().Select(identifier => identifier.Name).Concat(nodes.OfType<JsClass>().Select(type => type.Base ?? "")).Where(_names.Contains))
        pending.Enqueue(called);
    }

    return [.. Order.Where(written.ContainsKey).Select(name => (name, written[name]))];
  }

  /// <summary>The text of every helper as a profile has it, in the order a module lists them.</summary>
  /// <param name="native">Whether the profile has a capability.</param>
  /// <returns>Each helper's name and text.</returns>
  public static IEnumerable<(string Name, string Text)> Sources(Func<string, bool> native)
    => Order.Select(name => (name, string.Join('\n', Source(name, native).ReplaceLineEndings("\n").Split('\n').Select(line => Resolve(line, native)))));

  /// <summary>A line with everything a level decides written the way the profile has it.</summary>
  private static string Resolve(string line, Func<string, bool> native) {
    if (!line.Contains('@'))
      return line;

    return _placeholder.Replace(line, match => match.Value switch {
      "@let" => native("let") ? "let" : "var",
      "@const" => native("const") ? "const" : "var",
      "@imul(" => native("Math.imul") ? "Math.imul(" : "$imul(",
      "@fround(" => native("Math.fround") ? "Math.fround(" : "$fround(",
      _ => native("Math.trunc") ? "Math.trunc(" : "$trunc("
    });
  }

  /// <summary>The text of a helper for the profile, in the notation of this class.</summary>
  private static string Source(string name, Func<string, bool> native) => name switch {
    // What a level below ECMAScript 2015 lacks of Math.
    "$imul" => """
      function $imul(a, b) {
        @const high = (a >>> 16) & 0xFFFF;
        @const low = a & 0xFFFF;
        return (low * (b & 0xFFFF) + (((high * (b & 0xFFFF) + low * ((b >>> 16) & 0xFFFF)) << 16) >>> 0)) | 0;
      }
      """,
    "$f32a" => "@const $f32a = new Float32Array(1);",
    "$fround" => native("Float32Array")
      ? """
        function $fround(value) {
          $f32a[0] = value;
          return $f32a[0];
        }
        """
      : """
        function $fround(value) {
          if (value === 0 || value !== value || value === Infinity || value === -Infinity) return value;
          @const negative = value < 0;
          @let rest = negative ? -value : value;
          if (rest >= 3.4028235677973366e+38) return negative ? -Infinity : Infinity;
          @let unit = 1.1920928955078125e-7;
          while (rest >= 2) { rest /= 2; unit *= 2; }
          while (rest < 1 && unit > 1.401298464324817e-45) { rest *= 2; unit /= 2; }
          rest *= 8388608;
          @let whole = Math.floor(rest);
          @const part = rest - whole;
          if (part > 0.5 || (part === 0.5 && whole % 2 === 1)) ++whole;
          whole *= unit;
          return negative ? -whole : whole;
        }
        """,
    "$trunc" => """
      function $trunc(value) {
        return value < 0 ? Math.ceil(value) : Math.floor(value);
      }
      """,

    // The division of BigInts, which traps: the operation itself, with nothing weaker in the IR to write it with.
    // That of numbers, and below ECMAScript 2020 that of 64 bits (O0075), is the library's.
    "$div64" => Lines("function $div64(a, b) {", ZeroDivisor64, Unrepresentable64, "  return a / b;", "}"),
    "$rem64" => Lines("function $rem64(a, b) {", ZeroDivisor64, Unrepresentable64, "  return a % b;", "}"),
    "$udiv64" => Lines("function $udiv64(a, b) {", ZeroDivisor64, "  return a / b;", "}"),
    "$urem64" => Lines("function $urem64(a, b) {", ZeroDivisor64, "  return a % b;", "}"),

    // A number to a BigInt, saturating or trapping; to a number it is the library's.
    "$f2l" => """
      function $f2l(value, low, limit) {
        if (value !== value) return 0n;
        if (value >= limit) return BigInt(limit) - 1n;
        return value <= low ? BigInt(low) : BigInt(@trunc(value));
      }
      """,
    "$f2lc" => Lines(
      "function $f2lc(value, low, limit) {",
      "  @const whole = @trunc(value);",
      "  if (!(whole >= low && whole < limit)) " + Overflow,
      "  return BigInt(whole);",
      "}"),
    "$f32" => """
      function $f32(value) {
        @const negative = value < 0n;
        @let magnitude = negative ? -value : value;
        @let scale = 1;
        if (magnitude >= 9007199254740992n) {
          magnitude = (magnitude >> 11n) | (magnitude & 2047n ? 1n : 0n);
          scale = 2048;
        }
        @const result = @fround(Number(magnitude)) * scale;
        return negative ? -result : result;
      }
      """,
    // Arrays.
    "$get" => Lines("function $get(array, index) {", OutOfRange, "  return array[index];", "}"),
    "$set" => Lines("function $set(array, index, value) {", OutOfRange, "  array[index] = value;", "}"),
    "$new" => """
      function $new(length, zero) {
        @const array = [];
        for (@let index = 0; index < length; ++index) array[index] = zero;
        return array;
      }
      """,
    "$fill" => """
      function $fill(array, value) {
        for (@let index = 0; index < array.length; ++index) array[index] = value;
      }
      """,

    // The base library.

    // The bits of a floating-point number: through a DataView, or taken apart by arithmetic that is
    // exact because it only multiplies and divides by two.
    "$view" => "@const $view = new DataView(new ArrayBuffer(8));",
    "$bits64" => native(".getBigInt64")
      ? Lines("function $bits64(value) {", "  $view.setFloat64(0, value);", "  return $view.getBigInt64(0);", "}")
      : native("DataView")
        ? Lines("function $bits64(value) {", "  $view.setFloat64(0, value);", "  return { hi: $view.getUint32(0), lo: $view.getUint32(4) };", "}")
        : """
          function $bits64(value) {
            if (value !== value) return { hi: 2146959360, lo: 0 };
            @const sign = value < 0 || (value === 0 && 1 / value < 0) ? 2147483648 : 0;
            @let rest = value < 0 ? -value : value;
            if (rest === 0) return { hi: sign, lo: 0 };
            if (rest === Infinity) return { hi: sign + 2146435072, lo: 0 };
            @let exponent = 1023;
            while (rest >= 2) { rest /= 2; ++exponent; }
            while (rest < 1 && exponent > 1) { rest *= 2; --exponent; }
            if (rest < 1) exponent = 0; else rest -= 1;
            rest *= 4503599627370496;
            @const high = Math.floor(rest / 4294967296);
            return { hi: sign + exponent * 1048576 + high, lo: rest - high * 4294967296 };
          }
          """,
    "$bits32" => native("DataView")
      ? Lines("function $bits32(value) {", "  $view.setFloat32(0, value);", "  return $view.getInt32(0);", "}")
      : """
        function $bits32(value) {
          if (value !== value) return 2143289344;
          @const sign = value < 0 || (value === 0 && 1 / value < 0) ? -2147483648 : 0;
          @let rest = value < 0 ? -value : value;
          if (rest === 0) return sign;
          if (rest === Infinity) return sign | 2139095040;
          @let exponent = 127;
          while (rest >= 2) { rest /= 2; ++exponent; }
          while (rest < 1 && exponent > 1) { rest *= 2; --exponent; }
          return rest < 1 ? sign | (rest * 8388608) : sign | (exponent << 23) | ((rest - 1) * 8388608);
        }
        """,
    "$float64" => native(".setBigInt64")
      ? Lines("function $float64(value) {", "  $view.setBigInt64(0, value);", "  return $view.getFloat64(0);", "}")
      : native("DataView")
        ? Lines("function $float64(value) {", "  $view.setUint32(0, value.hi);", "  $view.setUint32(4, value.lo);", "  return $view.getFloat64(0);", "}")
        : """
          function $float64(value) {
            @const exponent = (value.hi >>> 20) & 2047;
            @const fraction = (value.hi & 1048575) * 4294967296 + value.lo;
            @let result = 1 + fraction / 4503599627370496;
            if (exponent === 2047) result = fraction !== 0 ? NaN : Infinity;
            else if (exponent === 0) result = fraction * 5e-324;
            for (@let up = exponent; up > 1023 && up < 2047; --up) result *= 2;
            for (@let down = exponent; down < 1023 && down > 0; ++down) result /= 2;
            return value.hi >= 2147483648 ? -result : result;
          }
          """,
    "$float32" => native("DataView")
      ? Lines("function $float32(value) {", "  $view.setInt32(0, value);", "  return $view.getFloat32(0);", "}")
      : """
        function $float32(value) {
          @const exponent = (value >>> 23) & 255;
          @const fraction = value & 8388607;
          @let result = 1 + fraction / 8388608;
          if (exponent === 255) result = fraction !== 0 ? NaN : Infinity;
          else if (exponent === 0) result = fraction * 1.401298464324817e-45;
          for (@let up = exponent; up > 127 && up < 255; --up) result *= 2;
          for (@let down = exponent; down < 127 && down > 0; ++down) result /= 2;
          return value < 0 ? -result : result;
        }
        """,
    "$key64" => """
      function $key64(value) {
        return value.hi + ":" + value.lo;
      }
      """,

    // Whole arrays and steps of an element.
    "$copy" => Lines("function $copy(source, from, target, to, length) {", RangeOutOfRange, "  target.set(source.subarray(from, from + length), to);", "}"),
    "$copya" => Lines(
      "function $copya(source, from, target, to, length) {",
      RangeOutOfRange,
      "  if (source === target && from < to) for (@let down = length - 1; down >= 0; --down) target[to + down] = source[from + down];",
      "  else for (@let up = 0; up < length; ++up) target[to + up] = source[from + up];",
      "}"),
    "$step" => Lines(
      "function $step(array, index, delta, post) {",
      OutOfRange,
      "  @const before = array[index];",
      "  array[index] = before + delta;",
      "  return post ? before : array[index];",
      "}"),
    "$stepi" => Lines(
      "function $stepi(array, index, delta, post, shift, signed) {",
      OutOfRange,
      "  @const before = array[index];",
      "  @const after = signed ? (before + delta) << shift >> shift : (before + delta) << shift >>> shift;",
      "  array[index] = after;",
      "  return post ? before : after;",
      "}"),
    "$stepf" => Lines(
      "function $stepf(array, index, delta, post) {",
      OutOfRange,
      "  @const before = array[index];",
      "  @const after = @fround(before + delta);",
      "  array[index] = after;",
      "  return post ? before : after;",
      "}"),
    "$stepck" => Lines(
      "function $stepck(array, index, delta, post, low, high) {",
      OutOfRange,
      "  @const before = array[index];",
      "  @const after = before + delta;",
      "  if (after < low || after > high) " + Overflow,
      "  array[index] = after;",
      "  return post ? before : after;",
      "}"),
    _ when ViewSource(name, native) is { } view => view,
    _ when PointerSource(name, native) is { } pointer => pointer,
    _ when StringSource(name, native) is { } text => text,
    _ when OptionalSource(name) is { } optional => optional,
    _ when GenericSource(name) is { } generic => generic,
    _ when ErrorSource(name, native) is { } error => error,
    _ when CollectionSource(name) is { } collection => collection,
    _ when NumberTextSource(name, native) is { } number => number,
    _ when EnumSource(name, native) is { } named => named,
    _ when SequenceSource(name) is { } sequence => sequence,
    _ => Lines("function $len(length) {", "  if (length < 0) " + Overflow, "  return length;", "}")
  };

  private static string Lines(params string[] lines) => string.Join('\n', lines);

}
