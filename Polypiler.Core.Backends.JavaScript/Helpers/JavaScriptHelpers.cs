using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The helper functions of generated code: the operations that can trap or need more than an
/// expression, and the replacements for what a language level lacks. A helper is written once, in a
/// notation that leaves open what differs between levels: <c>@let</c> and <c>@const</c> stand for
/// the keyword that declares a variable, <c>@imul(</c>, <c>@clz32(</c>, <c>@fround(</c> and
/// <c>@trunc(</c> for the function of <c>Math</c> or the helper that replaces it. Where a level
/// changes the whole body — 64-bit integers as <c>BigInt</c>s or as pairs of halves, the bits of a
/// floating-point number through a <c>DataView</c> or by arithmetic — a helper has one body per case.
/// </summary>
internal static class JavaScriptHelpers {

  private const string Overflow = "throw new RangeError(\"integer overflow\");";
  private const string ZeroDivisor = "  if (b === 0) throw new RangeError(\"divide by zero\");";
  private const string ZeroDivisor64 = "  if (b === 0n) throw new RangeError(\"divide by zero\");";
  private const string Unrepresentable = "  if (a === -2147483648 && b === -1) " + Overflow;
  private const string Unrepresentable64 = "  if (a === -9223372036854775808n && b === -1n) " + Overflow;
  private const string Unrepresentable64Halves = "  if (a.hi === 2147483648 && a.lo === 0 && b.hi === 4294967295 && b.lo === 4294967295) " + Overflow;
  private const string OutOfRange = "  if (index < 0 || index >= array.length) throw new RangeError(\"index out of range\");";
  private const string RangeOutOfRange = "  if (from < 0 || to < 0 || length < 0 || from + length > source.length || to + length > target.length) throw new RangeError(\"index out of range\");";
  private const string Joined = "{ hi: ((w[3] << 16) | w[2]) >>> 0, lo: ((w[1] << 16) | w[0]) >>> 0 }";

  /// <summary>The names of all helpers, in the order a module lists the ones it uses.</summary>
  public static readonly IReadOnlyList<string> Order = [
    "$imul", "$clz32", "$f32a", "$fround", "$trunc", "$sign",
    "$add64", "$sub64", "$neg64", "$not64", "$and64", "$or64", "$xor64", "$shl64", "$shr64", "$sar64",
    "$eq64", "$ucmp64", "$scmp64", "$mulw", "$mul64", "$mag64", "$udm64",
    "$div", "$rem", "$udiv", "$urem", "$div64", "$rem64", "$udiv64", "$urem64", "$ck",
    "$addck64s", "$addck64u", "$subck64s", "$subck64u", "$mulck64s", "$mulck64u", "$ck64s", "$ck64u",
    "$sx64", "$ul2d", "$l2d", "$d2l",
    "$f2i", "$f2l", "$f2ic", "$f2lc", "$f32", "$ul2f", "$l2f",
    "$get", "$set", "$new", "$fill", "$abs", "$abs64", "$min64", "$max64", "$smin64", "$umin64", "$smax64", "$umax64",
    "$sign64", "$fsign", "$round", "$rounda", "$rotl", "$rotr", "$rotl64", "$rotr64",
    "$pop", "$pop64", "$clz64", "$ctz", "$ctz64", "$view", "$bits64", "$bits32", "$float64", "$float32", "$key64",
    "$copy", "$copya", "$step", "$stepi", "$stepf", "$step64", "$stepck", "$stepck64s", "$stepck64u", "$len"
  ];

  private static readonly HashSet<string> _names = [.. Order];

  /// <summary>What a helper leaves open for the level to decide.</summary>
  private static readonly Regex _placeholder = new(@"@let|@const|@imul\(|@clz32\(|@fround\(|@trunc\(", RegexOptions.CultureInvariant);

  /// <summary>A name as the helpers have them: whether it is one of them is asked of <see cref="_names"/>.</summary>
  private static readonly Regex _helperName = new(@"\$[a-z0-9]+", RegexOptions.CultureInvariant);

  /// <summary>
  /// The helpers a module carries: the ones its code uses and the ones those call, each written for
  /// the profile.
  /// </summary>
  /// <param name="used">The names of the helpers the code of the module calls.</param>
  /// <param name="native">Whether the profile has a capability; asking for one it lacks and cannot replace reports it.</param>
  /// <returns>The helpers in their order, each with the lines of its text.</returns>
  public static IReadOnlyList<(string Name, string[] Lines)> For(IEnumerable<string> used, Func<string, bool> native) {
    var written = new Dictionary<string, string[]>();
    var pending = new Queue<string>(used);
    while (pending.Count > 0) {
      var name = pending.Dequeue();
      if (written.ContainsKey(name))
        continue;

      var lines = Source(name, native).ReplaceLineEndings("\n").Split('\n').Select(line => Resolve(line, native)).ToArray();
      written.Add(name, lines);
      foreach (var called in lines.SelectMany(line => _helperName.Matches(line)).Select(match => match.Value).Where(_names.Contains))
        pending.Enqueue(called);
    }

    return [.. Order.Where(written.ContainsKey).Select(name => (name, written[name]))];
  }

  /// <summary>A line with everything a level decides written the way the profile has it.</summary>
  private static string Resolve(string line, Func<string, bool> native) {
    if (!line.Contains('@'))
      return line;

    return _placeholder.Replace(line, match => match.Value switch {
      "@let" => native("let") ? "let" : "var",
      "@const" => native("const") ? "const" : "var",
      "@imul(" => native("Math.imul") ? "Math.imul(" : "$imul(",
      "@clz32(" => native("Math.clz32") ? "Math.clz32(" : "$clz32(",
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
    "$clz32" => """
      function $clz32(value) {
        value >>>= 0;
        if (value === 0) return 32;
        @let count = 0;
        if ((value & 0xFFFF0000) === 0) { count += 16; value <<= 16; }
        if ((value & 0xFF000000) === 0) { count += 8; value <<= 8; }
        if ((value & 0xF0000000) === 0) { count += 4; value <<= 4; }
        if ((value & 0xC0000000) === 0) { count += 2; value <<= 2; }
        return (value & 0x80000000) === 0 ? count + 1 : count;
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
    "$sign" => """
      function $sign(value) {
        return value < 0 ? -1 : value > 0 ? 1 : 0;
      }
      """,

    // 64-bit integers as pairs of unsigned 32-bit halves.
    "$add64" => """
      function $add64(a, b) {
        @const lo = a.lo + b.lo;
        return { hi: (a.hi + b.hi + (lo > 4294967295 ? 1 : 0)) >>> 0, lo: lo >>> 0 };
      }
      """,
    "$sub64" => """
      function $sub64(a, b) {
        @const lo = a.lo - b.lo;
        return { hi: (a.hi - b.hi - (lo < 0 ? 1 : 0)) >>> 0, lo: lo >>> 0 };
      }
      """,
    "$neg64" => """
      function $neg64(value) {
        return $sub64({ hi: 0, lo: 0 }, value);
      }
      """,
    "$not64" => """
      function $not64(value) {
        return { hi: ~value.hi >>> 0, lo: ~value.lo >>> 0 };
      }
      """,
    "$and64" => Bitwise("$and64", "&"),
    "$or64" => Bitwise("$or64", "|"),
    "$xor64" => Bitwise("$xor64", "^"),
    "$shl64" => """
      function $shl64(value, count) {
        count &= 63;
        if (count === 0) return value;
        if (count >= 32) return { hi: (value.lo << (count - 32)) >>> 0, lo: 0 };
        return { hi: ((value.hi << count) | (value.lo >>> (32 - count))) >>> 0, lo: (value.lo << count) >>> 0 };
      }
      """,
    "$shr64" => """
      function $shr64(value, count) {
        count &= 63;
        if (count === 0) return value;
        if (count >= 32) return { hi: 0, lo: value.hi >>> (count - 32) };
        return { hi: value.hi >>> count, lo: ((value.lo >>> count) | (value.hi << (32 - count))) >>> 0 };
      }
      """,
    "$sar64" => """
      function $sar64(value, count) {
        count &= 63;
        if (count === 0) return value;
        if (count >= 32) return { hi: (value.hi >> 31) >>> 0, lo: (value.hi >> (count - 32)) >>> 0 };
        return { hi: (value.hi >> count) >>> 0, lo: ((value.lo >>> count) | (value.hi << (32 - count))) >>> 0 };
      }
      """,
    "$eq64" => """
      function $eq64(a, b) {
        return a.hi === b.hi && a.lo === b.lo;
      }
      """,
    "$ucmp64" => """
      function $ucmp64(a, b) {
        return a.hi !== b.hi ? (a.hi < b.hi ? -1 : 1) : a.lo !== b.lo ? (a.lo < b.lo ? -1 : 1) : 0;
      }
      """,
    "$scmp64" => """
      function $scmp64(a, b) {
        @const x = a.hi | 0;
        @const y = b.hi | 0;
        return x !== y ? (x < y ? -1 : 1) : a.lo !== b.lo ? (a.lo < b.lo ? -1 : 1) : 0;
      }
      """,
    // Knuth, The Art of Computer Programming, volume 2, 4.3.1, algorithm M, on digits of 16 bits:
    // the product of two digits and two carries is below 2^32 and so exact in a number.
    "$mulw" => """
      function $mulw(a, b) {
        @const u = [a.lo & 0xFFFF, a.lo >>> 16, a.hi & 0xFFFF, a.hi >>> 16];
        @const v = [b.lo & 0xFFFF, b.lo >>> 16, b.hi & 0xFFFF, b.hi >>> 16];
        @const w = [0, 0, 0, 0, 0, 0, 0, 0];
        for (@let j = 0; j < 4; ++j) {
          @let carry = 0;
          for (@let i = 0; i < 4; ++i) {
            @const t = u[i] * v[j] + w[i + j] + carry;
            w[i + j] = t & 0xFFFF;
            carry = t >>> 16;
          }
          w[j + 4] = carry;
        }
        return w;
      }
      """,
    "$mul64" => $$"""
      function $mul64(a, b) {
        @const w = $mulw(a, b);
        return {{Joined}};
      }
      """,
    "$mag64" => """
      function $mag64(value) {
        return value.hi >= 2147483648 ? $neg64(value) : value;
      }
      """,
    // Knuth, The Art of Computer Programming, volume 2, 4.3.1, algorithm D, on digits of 16 bits;
    // a divisor of one digit is divided by directly, as the algorithm asks for two digits or more.
    "$udm64" => $$"""
      function $udm64(a, b) {
        @const v = [b.lo & 0xFFFF, b.lo >>> 16, b.hi & 0xFFFF, b.hi >>> 16];
        @let n = 4;
        while (n > 0 && v[n - 1] === 0) --n;
        if (n === 0) throw new RangeError("divide by zero");
        @const u = [a.lo & 0xFFFF, a.lo >>> 16, a.hi & 0xFFFF, a.hi >>> 16, 0];
        @const w = [0, 0, 0, 0];
        if (n === 1) {
          @let rest = 0;
          for (@let k = 3; k >= 0; --k) {
            @const t = rest * 65536 + u[k];
            w[k] = Math.floor(t / v[0]);
            rest = t - w[k] * v[0];
          }
          return [{{Joined}}, { hi: 0, lo: rest }];
        }
        @let shift = 0;
        while ((v[n - 1] << shift) < 32768) ++shift;
        for (@let i = n - 1; i > 0; --i) v[i] = ((v[i] << shift) | (v[i - 1] >>> (16 - shift))) & 0xFFFF;
        v[0] = (v[0] << shift) & 0xFFFF;
        for (@let i = 4; i > 0; --i) u[i] = ((u[i] << shift) | (u[i - 1] >>> (16 - shift))) & 0xFFFF;
        u[0] = (u[0] << shift) & 0xFFFF;
        for (@let j = 4 - n; j >= 0; --j) {
          @const top = u[j + n] * 65536 + u[j + n - 1];
          @let digit = Math.floor(top / v[n - 1]);
          @let rest = top - digit * v[n - 1];
          while (digit >= 65536 || digit * v[n - 2] > rest * 65536 + u[j + n - 2]) {
            --digit;
            rest += v[n - 1];
            if (rest >= 65536) break;
          }
          @let borrow = 0;
          @let carry = 0;
          for (@let i = 0; i < n; ++i) {
            @const product = digit * v[i] + carry;
            carry = Math.floor(product / 65536);
            @const difference = u[i + j] - borrow - (product - carry * 65536);
            borrow = difference < 0 ? 1 : 0;
            u[i + j] = difference & 0xFFFF;
          }
          @const last = u[j + n] - borrow - carry;
          u[j + n] = last & 0xFFFF;
          if (last < 0) {
            --digit;
            carry = 0;
            for (@let i = 0; i < n; ++i) {
              @const sum = u[i + j] + v[i] + carry;
              u[i + j] = sum & 0xFFFF;
              carry = sum >>> 16;
            }
            u[j + n] = (u[j + n] + carry) & 0xFFFF;
          }
          w[j] = digit;
        }
        @const r = [0, 0, 0, 0];
        for (@let i = 0; i < n; ++i) r[i] = (u[i] >>> shift) | ((u[i + 1] << (16 - shift)) & 0xFFFF);
        return [{{Joined}}, { hi: ((r[3] << 16) | r[2]) >>> 0, lo: ((r[1] << 16) | r[0]) >>> 0 }];
      }
      """,

    // Division, which traps.
    "$div" => Lines("function $div(a, b) {", ZeroDivisor, Unrepresentable, "  return (a / b) | 0;", "}"),
    "$rem" => Lines("function $rem(a, b) {", ZeroDivisor, Unrepresentable, "  return (a % b) | 0;", "}"),
    "$udiv" => Lines("function $udiv(a, b) {", ZeroDivisor, "  return (a / b) >>> 0;", "}"),
    "$urem" => Lines("function $urem(a, b) {", ZeroDivisor, "  return a % b;", "}"),
    "$div64" => native("BigInt")
      ? Lines("function $div64(a, b) {", ZeroDivisor64, Unrepresentable64, "  return a / b;", "}")
      : Lines(
        "function $div64(a, b) {",
        "  @const quotient = $udm64($mag64(a), $mag64(b))[0];",
        Unrepresentable64Halves,
        "  return (a.hi ^ b.hi) < 0 ? $neg64(quotient) : quotient;",
        "}"),
    "$rem64" => native("BigInt")
      ? Lines("function $rem64(a, b) {", ZeroDivisor64, Unrepresentable64, "  return a % b;", "}")
      : Lines(
        "function $rem64(a, b) {",
        "  @const rest = $udm64($mag64(a), $mag64(b))[1];",
        Unrepresentable64Halves,
        "  return a.hi >= 2147483648 ? $neg64(rest) : rest;",
        "}"),
    "$udiv64" => native("BigInt")
      ? Lines("function $udiv64(a, b) {", ZeroDivisor64, "  return a / b;", "}")
      : Lines("function $udiv64(a, b) {", "  return $udm64(a, b)[0];", "}"),
    "$urem64" => native("BigInt")
      ? Lines("function $urem64(a, b) {", ZeroDivisor64, "  return a % b;", "}")
      : Lines("function $urem64(a, b) {", "  return $udm64(a, b)[1];", "}"),

    // Checked arithmetic and checked conversions.
    "$ck" => Lines("function $ck(value, low, high) {", "  if (value < low || value > high) " + Overflow, "  return value;", "}"),
    "$addck64s" => Lines(
      "function $addck64s(a, b) {",
      "  @const result = $add64(a, b);",
      "  if (((a.hi ^ result.hi) & (b.hi ^ result.hi)) < 0) " + Overflow,
      "  return result;",
      "}"),
    "$addck64u" => Lines(
      "function $addck64u(a, b) {",
      "  @const result = $add64(a, b);",
      "  if ($ucmp64(result, a) < 0) " + Overflow,
      "  return result;",
      "}"),
    "$subck64s" => Lines(
      "function $subck64s(a, b) {",
      "  @const result = $sub64(a, b);",
      "  if (((a.hi ^ b.hi) & (a.hi ^ result.hi)) < 0) " + Overflow,
      "  return result;",
      "}"),
    "$subck64u" => Lines(
      "function $subck64u(a, b) {",
      "  if ($ucmp64(a, b) < 0) " + Overflow,
      "  return $sub64(a, b);",
      "}"),
    "$mulck64s" => Lines(
      "function $mulck64s(a, b) {",
      "  @const negative = ((a.hi ^ b.hi) & 0x80000000) !== 0;",
      "  @const w = $mulw($mag64(a), $mag64(b));",
      "  @const product = " + Joined + ";",
      "  if ((w[4] | w[5] | w[6] | w[7]) !== 0 || product.hi > 2147483648 || (product.hi === 2147483648 && (product.lo !== 0 || !negative))) " + Overflow,
      "  return negative ? $neg64(product) : product;",
      "}"),
    "$mulck64u" => Lines(
      "function $mulck64u(a, b) {",
      "  @const w = $mulw(a, b);",
      "  if ((w[4] | w[5] | w[6] | w[7]) !== 0) " + Overflow,
      "  return " + Joined + ";",
      "}"),
    "$ck64s" => Lines(
      "function $ck64s(value, low, high) {",
      "  if ($scmp64(value, low) < 0 || $scmp64(value, high) > 0) " + Overflow,
      "  return value;",
      "}"),
    "$ck64u" => Lines(
      "function $ck64u(value, low, high) {",
      "  if ($ucmp64(value, low) < 0 || $ucmp64(value, high) > 0) " + Overflow,
      "  return value;",
      "}"),

    // Conversions between pairs of halves and numbers.
    "$sx64" => """
      function $sx64(value) {
        return { hi: value < 0 ? 4294967295 : 0, lo: value >>> 0 };
      }
      """,
    "$ul2d" => """
      function $ul2d(value) {
        return value.hi * 4294967296 + value.lo;
      }
      """,
    "$l2d" => """
      function $l2d(value) {
        return value.hi >= 2147483648 ? -$ul2d($neg64(value)) : $ul2d(value);
      }
      """,
    "$d2l" => """
      function $d2l(value) {
        @const magnitude = value < 0 ? -value : value + 0;
        @const hi = Math.floor(magnitude / 4294967296);
        @const result = { hi: hi, lo: magnitude - hi * 4294967296 };
        return value < 0 ? $neg64(result) : result;
      }
      """,
    "$f2i" => """
      function $f2i(value, low, high) {
        if (value !== value) return 0;
        return value <= low ? low : value >= high ? high : @trunc(value) + 0;
      }
      """,
    "$f2l" => native("BigInt")
      ? """
        function $f2l(value, low, limit) {
          if (value !== value) return 0n;
          if (value >= limit) return BigInt(limit) - 1n;
          return value <= low ? BigInt(low) : BigInt(@trunc(value));
        }
        """
      : """
        function $f2l(value, low, limit) {
          if (value !== value) return { hi: 0, lo: 0 };
          if (value >= limit) return { hi: low < 0 ? 2147483647 : 4294967295, lo: 4294967295 };
          return $d2l(value <= low ? low : @trunc(value));
        }
        """,
    "$f2ic" => Lines(
      "function $f2ic(value, low, high) {",
      "  @const whole = @trunc(value);",
      "  if (!(whole >= low && whole <= high)) " + Overflow,
      "  return whole + 0;",
      "}"),
    "$f2lc" => Lines(
      "function $f2lc(value, low, limit) {",
      "  @const whole = @trunc(value);",
      "  if (!(whole >= low && whole < limit)) " + Overflow,
      native("BigInt") ? "  return BigInt(whole);" : "  return $d2l(whole);",
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
    // Rounded once: above 2^53 the eleven lowest bits are shifted out, and the lowest bit that stays
    // remembers whether any of them was set, which is all the rounding to binary32 needs of them.
    "$ul2f" => """
      function $ul2f(value) {
        if (value.hi < 2097152) return @fround(value.hi * 4294967296 + value.lo);
        @const lo = ((value.lo >>> 11) | (value.hi << 21) | ((value.lo & 2047) !== 0 ? 1 : 0)) >>> 0;
        return @fround((value.hi >>> 11) * 4294967296 + lo) * 2048;
      }
      """,
    "$l2f" => """
      function $l2f(value) {
        return value.hi >= 2147483648 ? -$ul2f($neg64(value)) : $ul2f(value);
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
    "$abs" => Lines("function $abs(value, low) {", "  if (value === low) " + Overflow, "  return value < 0 ? -value : value;", "}"),
    "$abs64" => Lines(
      "function $abs64(value) {",
      "  if (value.hi === 2147483648 && value.lo === 0) " + Overflow,
      "  return $mag64(value);",
      "}"),
    "$min64" => Lines("function $min64(a, b) {", "  return a < b ? a : b;", "}"),
    "$max64" => Lines("function $max64(a, b) {", "  return a > b ? a : b;", "}"),
    "$smin64" => Lines("function $smin64(a, b) {", "  return $scmp64(a, b) < 0 ? a : b;", "}"),
    "$umin64" => Lines("function $umin64(a, b) {", "  return $ucmp64(a, b) < 0 ? a : b;", "}"),
    "$smax64" => Lines("function $smax64(a, b) {", "  return $scmp64(a, b) > 0 ? a : b;", "}"),
    "$umax64" => Lines("function $umax64(a, b) {", "  return $ucmp64(a, b) > 0 ? a : b;", "}"),
    "$sign64" => native("BigInt")
      ? Lines("function $sign64(value) {", "  return value < 0n ? -1 : value > 0n ? 1 : 0;", "}")
      : Lines("function $sign64(value) {", "  return value.hi >= 2147483648 ? -1 : (value.hi | value.lo) !== 0 ? 1 : 0;", "}"),
    "$fsign" => """
      function $fsign(value) {
        if (value !== value) throw new RangeError("invalid operation");
        return value < 0 ? -1 : value > 0 ? 1 : 0;
      }
      """,
    "$round" => """
      function $round(value) {
        @const rounded = Math.round(value);
        return rounded - value === 0.5 && rounded % 2 !== 0 ? rounded - 1 : rounded;
      }
      """,
    "$rounda" => Lines("function $rounda(value) {", "  return value < 0 ? -Math.round(-value) : Math.round(value);", "}"),
    "$rotl" => Lines("function $rotl(value, count) {", "  return ((value << count) | (value >>> (-count & 31))) >>> 0;", "}"),
    "$rotr" => Lines("function $rotr(value, count) {", "  return ((value >>> count) | (value << (-count & 31))) >>> 0;", "}"),
    "$rotl64" => native("BigInt")
      ? """
        function $rotl64(value, count) {
          @const by = BigInt(count & 63);
          return BigInt.asUintN(64, (value << by) | (value >> (64n - by)));
        }
        """
      : """
        function $rotl64(value, count) {
          count &= 63;
          return count === 0 ? value : $or64($shl64(value, count), $shr64(value, 64 - count));
        }
        """,
    "$rotr64" => native("BigInt")
      ? """
        function $rotr64(value, count) {
          @const by = BigInt(count & 63);
          return BigInt.asUintN(64, (value >> by) | (value << (64n - by)));
        }
        """
      : """
        function $rotr64(value, count) {
          count &= 63;
          return count === 0 ? value : $or64($shr64(value, count), $shl64(value, 64 - count));
        }
        """,
    "$pop" => """
      function $pop(value) {
        value -= (value >>> 1) & 0x55555555;
        value = (value & 0x33333333) + ((value >>> 2) & 0x33333333);
        return @imul((value + (value >>> 4)) & 0x0F0F0F0F, 0x01010101) >>> 24;
      }
      """,
    "$pop64" => native("BigInt")
      ? Lines("function $pop64(value) {", "  return $pop(Number(value >> 32n)) + $pop(Number(value & 0xFFFFFFFFn));", "}")
      : Lines("function $pop64(value) {", "  return $pop(value.hi) + $pop(value.lo);", "}"),
    "$clz64" => native("BigInt")
      ? """
        function $clz64(value) {
          @const high = Number(value >> 32n);
          return high !== 0 ? @clz32(high) : 32 + @clz32(Number(value & 0xFFFFFFFFn));
        }
        """
      : """
        function $clz64(value) {
          return value.hi !== 0 ? @clz32(value.hi) : 32 + @clz32(value.lo);
        }
        """,
    "$ctz" => Lines("function $ctz(value) {", "  return value === 0 ? 32 : 31 - @clz32(value & -value);", "}"),
    "$ctz64" => native("BigInt")
      ? """
        function $ctz64(value) {
          @const low = Number(value & 0xFFFFFFFFn);
          return low !== 0 ? $ctz(low) : 32 + $ctz(Number(value >> 32n));
        }
        """
      : """
        function $ctz64(value) {
          return value.lo !== 0 ? $ctz(value.lo) : 32 + $ctz(value.hi);
        }
        """,

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
    "$step64" => Lines(
      "function $step64(array, index, up, post) {",
      OutOfRange,
      "  @const before = array[index];",
      "  @const after = up ? $add64(before, { hi: 0, lo: 1 }) : $sub64(before, { hi: 0, lo: 1 });",
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
    "$stepck64s" => CheckedStep("s"),
    "$stepck64u" => CheckedStep("u"),
    _ => Lines("function $len(length) {", "  if (length < 0) " + Overflow, "  return length;", "}")
  };

  private static string Lines(params string[] lines) => string.Join('\n', lines);

  private static string Bitwise(string name, string sign)
    => Lines($"function {name}(a, b) {{", $"  return {{ hi: (a.hi {sign} b.hi) >>> 0, lo: (a.lo {sign} b.lo) >>> 0 }};", "}");

  private static string CheckedStep(string signedness) => Lines(
    $"function $stepck64{signedness}(array, index, up, post) {{",
    OutOfRange,
    "  @const before = array[index];",
    $"  @const after = up ? $addck64{signedness}(before, {{ hi: 0, lo: 1 }}) : $subck64{signedness}(before, {{ hi: 0, lo: 1 }});",
    "  array[index] = after;",
    "  return post ? before : after;",
    "}");

}
