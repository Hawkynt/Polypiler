// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The helpers of the conversions between numbers and text. The rules of .NET: the shortest digits of
/// <c>String</c> laid out as .NET lays them out, the shortest digits of a binary32 by <c>toPrecision</c>
/// (a tie to the even digit, as .NET's shortest digits have it), the exact decimal digits of a double
/// for <c>"F&lt;n&gt;"</c> (a tie to the even digit), and the grammar of <c>Parse</c> with the
/// invariant culture, which throws the <c>FormatError</c> and <c>OverflowError</c> of the IR's traps (both <c>RangeError</c>s); the digits of a radix,
/// also of the halves of a 64-bit integer below ECMAScript 2020.
/// </summary>
internal static partial class JavaScriptHelpers {

  /// <summary>Every helper of the conversions with its text for a profile.</summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> NumberTextHelpers => [
    Same("$radix", """
      function $radix(x, radix, min, upper) {
        @let s = (x < 0 ? -x : x).toString(radix);
        while (s.length < min) s = "0" + s;
        if (upper) s = s.toUpperCase();
        return x < 0 ? "-" + s : s;
      }
      """),
    Same("$radix64", """
      function $radix64(x, radix, min, upper, signed) {
        @const negative = signed && x.hi >= 2147483648;
        @const v = negative ? { hi: (~x.hi + (x.lo === 0 ? 1 : 0)) >>> 0, lo: (0 - x.lo) >>> 0 } : x;
        @const w = [v.lo & 0xFFFF, v.lo >>> 16, v.hi & 0xFFFF, v.hi >>> 16];
        @let s = "";
        do {
          @let rest = 0;
          for (@let k = 3; k >= 0; --k) {
            @const t = rest * 65536 + w[k];
            w[k] = Math.floor(t / radix);
            rest = t - w[k] * radix;
          }
          s = "0123456789abcdefghijklmnopqrstuvwxyz".charAt(rest) + s;
        } while (w[0] !== 0 || w[1] !== 0 || w[2] !== 0 || w[3] !== 0);
        while (s.length < min) s = "0" + s;
        if (upper) s = s.toUpperCase();
        return negative ? "-" + s : s;
      }
      """),
    Same("$layoutNet", """
      function $layoutNet(text, most) {
        @let sign = "";
        if (text.charAt(0) === "-") {
          sign = "-";
          text = text.substring(1);
        }
        @let exponent = 0;
        @const e = text.indexOf("e");
        if (e >= 0) {
          exponent = +text.substring(e + 1);
          text = text.substring(0, e);
        }
        @const point = text.indexOf(".");
        @let digits = point < 0 ? text : text.substring(0, point) + text.substring(point + 1);
        exponent += (point < 0 ? text.length : point) - 1;
        while (digits.charAt(0) === "0") {
          digits = digits.substring(1);
          --exponent;
        }
        while (digits.charAt(digits.length - 1) === "0") digits = digits.substring(0, digits.length - 1);
        if (exponent < -4 || exponent >= most) {
          @const power = exponent < 0 ? -exponent : exponent;
          return sign + digits.charAt(0) + (digits.length > 1 ? "." + digits.substring(1) : "") + (exponent < 0 ? "E-" : "E+") + (power < 10 ? "0" : "") + power;
        }
        if (exponent < 0) {
          for (@let k = -1; k > exponent; --k) digits = "0" + digits;
          return sign + "0." + digits;
        }
        while (digits.length <= exponent) digits += "0";
        return sign + digits.substring(0, exponent + 1) + (digits.length > exponent + 1 ? "." + digits.substring(exponent + 1) : "");
      }
      """),
    Same("$fmtNet", """
      function $fmtNet(x) {
        if (x !== x) return "NaN";
        if (x === 0) return 1 / x < 0 ? "-0" : "0";
        if (x === Infinity || x === -Infinity) return x < 0 ? "-Infinity" : "Infinity";
        return $layoutNet(String(x), 17);
      }
      """),
    Same("$fmtNet32", """
      function $fmtNet32(x) {
        if (x !== x) return "NaN";
        if (x === 0) return 1 / x < 0 ? "-0" : "0";
        if (x === Infinity || x === -Infinity) return x < 0 ? "-Infinity" : "Infinity";
        @let p = 1;
        while (p < 9 && @fround(+x.toPrecision(p)) !== x) ++p;
        @let text = x.toPrecision(p);
        // toPrecision rounds a tie up, the shortest digits of .NET go to the even digit when it reads back as well.
        @const longer = x.toPrecision(p + 1);
        @const end = longer.indexOf("e") < 0 ? longer.length : longer.indexOf("e");
        @const last = (text.indexOf("e") < 0 ? text.length : text.indexOf("e")) - 1;
        if (+longer === x && longer.charAt(end - 1) === "5" && +text.charAt(last) % 2 === 1) {
          @const lower = text.substring(0, last) + (+text.charAt(last) - 1) + text.substring(last + 1);
          if (@fround(+lower) === x) text = lower;
        }
        return $layoutNet(text, 9);
      }
      """),
    Same("$exact", """
      function $exact(m) {
        @let e = 0;
        while (m >= 9007199254740992) {
          m /= 2;
          ++e;
        }
        while (m % 1 !== 0) {
          m *= 2;
          --e;
        }
        @const low = m % 10000000;
        @const rest = (m - low) / 10000000;
        @const limbs = [low, rest % 10000000, (rest - rest % 10000000) / 10000000];
        for (@let k = e < 0 ? -e : e; k > 0; ) {
          @const step = e < 0 ? (k < 10 ? k : 10) : (k < 23 ? k : 23);
          @const factor = Math.pow(e < 0 ? 5 : 2, step);
          @let carry = 0;
          for (@let i = 0; i < limbs.length; ++i) {
            @const t = limbs[i] * factor + carry;
            limbs[i] = t % 10000000;
            carry = (t - limbs[i]) / 10000000;
          }
          while (carry > 0) {
            limbs.push(carry % 10000000);
            carry = (carry - carry % 10000000) / 10000000;
          }
          k -= step;
        }
        while (limbs.length > 1 && limbs[limbs.length - 1] === 0) limbs.pop();
        @let digits = "" + limbs[limbs.length - 1];
        for (@let i = limbs.length - 2; i >= 0; --i) {
          @const part = "" + limbs[i];
          digits += "0000000".substring(part.length) + part;
        }
        return { digits: digits, scale: e < 0 ? e : 0 };
      }
      """),
    Same("$fmtVb", """
      function $fmtVb(x, p, s) {
        if (x !== x) return "-1.#IND";
        if (x === Infinity || x === -Infinity) return x < 0 ? "-1.#INF" : "1.#INF";
        if (x === 0) return "0";
        @const exact = $exact(x < 0 ? -x : x);
        @let digits = exact.digits;
        @let first = digits.length - 1 + exact.scale;
        if (digits.length > p) {
          @let rest = false;
          for (@let k = p + 1; k < digits.length && !rest; ++k) rest = digits.charAt(k) !== "0";
          @let up = digits.charAt(p) > "5" || digits.charAt(p) === "5" && (rest || +digits.charAt(p - 1) % 2 === 1);
          digits = digits.substring(0, p);
          for (@let k = p - 1; up && k >= 0; --k) {
            up = digits.charAt(k) === "9";
            digits = digits.substring(0, k) + (up ? 0 : +digits.charAt(k) + 1) + digits.substring(k + 1);
          }
          if (up) {
            digits = "1" + digits.substring(0, p - 1);
            ++first;
          }
        }
        while (digits.length > 1 && digits.charAt(digits.length - 1) === "0") digits = digits.substring(0, digits.length - 1);
        @const sign = x < 0 ? "-" : "";
        if (first < -4 || first >= p) {
          @const m = first < 0 ? -first : first;
          return sign + digits.charAt(0) + (digits.length > 1 ? s + digits.substring(1) : "") + (first < 0 ? "E-" : "E+") + (m < 10 ? "0" : "") + m;
        }
        if (first < 0) {
          @let zeros = "";
          for (@let k = 1; k < -first; ++k) zeros += "0";
          return sign + "0" + s + zeros + digits;
        }
        while (digits.length < first + 1) digits += "0";
        return sign + digits.substring(0, first + 1) + (digits.length > first + 1 ? s + digits.substring(first + 1) : "");
      }
      """),
    Same("$fixedNet", """
      function $fixedNet(x, d) {
        if (x !== x) return "NaN";
        if (x === Infinity || x === -Infinity) return x < 0 ? "-Infinity" : "Infinity";
        @const exact = $exact(x < 0 ? -x : x);
        @let digits = exact.digits;
        for (@let k = 0; k < d; ++k) digits += "0";
        @const dropped = -exact.scale;
        while (digits.length <= dropped + d) digits = "0" + digits;
        @const kept = digits.length - dropped;
        @let up = false;
        if (dropped > 0) {
          @const first = digits.charAt(kept);
          @let rest = false;
          for (@let k = kept + 1; k < digits.length && !rest; ++k) rest = digits.charAt(k) !== "0";
          up = first > "5" || first === "5" && (rest || +digits.charAt(kept - 1) % 2 === 1);
        }
        digits = digits.substring(0, kept);
        for (@let k = kept - 1; up && k >= 0; --k) {
          up = digits.charAt(k) === "9";
          digits = digits.substring(0, k) + (up ? 0 : +digits.charAt(k) + 1) + digits.substring(k + 1);
        }
        if (up) digits = "1" + digits;
        while (digits.length > d + 1 && digits.charAt(0) === "0") digits = digits.substring(1);
        return (x < 0 || 1 / x < 0 ? "-" : "") + (d === 0 ? digits : digits.substring(0, digits.length - d) + "." + digits.substring(digits.length - d));
      }
      """),
    Same("$netDigits", """
      function $netDigits(s) {
        @const white = "\u0009\n\u000B\u000C\u000D ";
        @let at = 0;
        @let end = s.length;
        while (at < end && white.indexOf(s.charAt(at)) >= 0) ++at;
        while (end > at && s.charAt(end - 1) === "\u0000") --end;
        while (end > at && white.indexOf(s.charAt(end - 1)) >= 0) --end;
        @const negative = at < end && s.charAt(at) === "-";
        if (at < end && (negative || s.charAt(at) === "+")) ++at;
        if (at === end) throw new FormatError("invalid format");
        for (@let k = at; k < end; ++k) if (s.charAt(k) < "0" || s.charAt(k) > "9") throw new FormatError("invalid format");
        return (negative ? "-" : "") + s.substring(at, end);
      }
      """),
    Same("$parseNet", """
      function $parseNet(s, single) {
        @const special = $trimNet(s, true, true);
        @const symbol = special.charAt(0) === "+" || special.charAt(0) === "-" ? special.substring(1) : special;
        if (symbol.length === 8 && symbol.toUpperCase() === "INFINITY") return special.charAt(0) === "-" ? -Infinity : Infinity;
        if (symbol.length === 3 && symbol.toUpperCase() === "NAN") return NaN;
        @const white = "\u0009\n\u000B\u000C\u000D ";
        @let at = 0;
        @let end = s.length;
        while (at < end && white.indexOf(s.charAt(at)) >= 0) ++at;
        while (end > at && s.charAt(end - 1) === "\u0000") --end;
        while (end > at && white.indexOf(s.charAt(end - 1)) >= 0) --end;
        @const negative = at < end && s.charAt(at) === "-";
        if (at < end && (s.charAt(at) === "+" || negative)) ++at;
        @let digits = "";
        @let count = 0;
        @let scale = 0;
        @let point = false;
        for (; at < end; ++at) {
          @const unit = s.charAt(at);
          if (unit >= "0" && unit <= "9") {
            if (digits !== "" || unit !== "0") digits += unit;
            if (point) --scale;
            ++count;
          } else if (unit === "." && !point) {
            point = true;
          } else if (unit !== "," || point || count === 0) {
            break;
          }
        }
        if (count > 0 && at < end && (s.charAt(at) === "e" || s.charAt(at) === "E")) {
          @let mark = at + 1;
          @const down = mark < end && s.charAt(mark) === "-";
          if (mark < end && (s.charAt(mark) === "+" || down)) ++mark;
          @let power = 0;
          @const first = mark;
          for (; mark < end && s.charAt(mark) >= "0" && s.charAt(mark) <= "9"; ++mark) if (power < 100000) power = power * 10 + +s.charAt(mark);
          if (mark > first) {
            scale += down ? -power : power;
            at = mark;
          }
        }
        if (count === 0 || at < end) throw new FormatError("invalid format");
        @const value = +((negative ? "-" : "") + (digits === "" ? "0" : digits) + "e" + scale);
        if (!single) return value;
        @const near = @fround(value);
        @const far = value + (value - near);
        if (near === value || near === Infinity || near === -Infinity || @fround(far) !== far) return near;
        // As a double the text lies between two floats; as a decimal it may lie on either side of that midpoint.
        @const exact = $exact(negative ? -value : value);
        @let order = digits.length + scale - exact.digits.length - exact.scale;
        for (@let k = 0; order === 0 && k < digits.length + exact.digits.length; ++k) {
          order = (k < digits.length ? +digits.charAt(k) : 0) - (k < exact.digits.length ? +exact.digits.charAt(k) : 0);
        }
        return order === 0 ? near : (order > 0) === (negative ? far < near : far > near) ? far : near;
      }
      """),
    Same("$parseNetInt", """
      function $parseNetInt(s, low, high) {
        @const digits = $netDigits(s);
        @const value = +digits + 0;
        if (value < low || value > high) throw new OverflowError("integer overflow");
        return value;
      }
      """),
    ("$parseNet64", native => native("BigInt")
      ? """
        function $parseNet64(s, signed) {
          @const value = BigInt($netDigits(s));
          if (signed ? value < -9223372036854775808n || value > 9223372036854775807n : value < 0n || value > 18446744073709551615n) throw new OverflowError("integer overflow");
          return value;
        }
        """
      : """
        function $parseNet64(s, signed) {
          @const digits = $netDigits(s);
          @const negative = digits.charAt(0) === "-";
          @const w = [0, 0, 0, 0];
          @let overflow = false;
          for (@let k = negative ? 1 : 0; k < digits.length; ++k) {
            @let carry = digits.charCodeAt(k) - 48;
            for (@let i = 0; i < 4; ++i) {
              @const t = w[i] * 10 + carry;
              w[i] = t % 65536;
              carry = (t - w[i]) / 65536;
            }
            if (carry !== 0) overflow = true;
          }
          @const magnitude = { hi: w[3] * 65536 + w[2], lo: w[1] * 65536 + w[0] };
          if (overflow || (signed ? magnitude.hi > 2147483648 || magnitude.hi === 2147483648 && (magnitude.lo !== 0 || !negative) : negative && (magnitude.hi !== 0 || magnitude.lo !== 0))) throw new OverflowError("integer overflow");
          return negative ? { hi: (~magnitude.hi + (magnitude.lo === 0 ? 1 : 0)) >>> 0, lo: (0 - magnitude.lo) >>> 0 } : magnitude;
        }
        """)
  ];

  /// <summary>The names of the helpers of the conversions, in the order a module lists them, after those of the strings.</summary>
  private static IEnumerable<string> NumberTextOrder => NumberTextHelpers.Select(helper => helper.Name);

  private static readonly Dictionary<string, Func<Func<string, bool>, string>> _numberText = NumberTextHelpers.ToDictionary(helper => helper.Name, helper => helper.Text);

  /// <summary>The text of a helper of the conversions for the profile, or <see langword="null"/> for a name that is none.</summary>
  private static string? NumberTextSource(string name, Func<string, bool> native) => _numberText.TryGetValue(name, out var text) ? text(native) : null;

}
