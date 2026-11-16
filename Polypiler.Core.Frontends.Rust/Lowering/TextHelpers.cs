using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>
/// The helpers of text: Rust's UTF-8 view of a string - its byte length, the code unit of a byte
/// offset, its bytes, its chars - computed from the IR's UTF-16 code units, the order of code points,
/// Rust's whitespace (the Unicode <c>White_Space</c> property), and the parsing of integers as
/// <c>core::num</c>'s <c>from_str_radix</c> does it in base 10. They are Rust, written in the subset
/// this frontend imports with the IR's string operations as the functions <c>__units</c>,
/// <c>__unit</c>, <c>__substring</c>, <c>__from_unit</c> and <c>__index_of</c>, and lowered with the
/// module, so every backend emits them through its ordinary path. <c>@</c> stands for the prefix
/// that keeps their names apart from the crate's.
/// </summary>
internal static class TextHelpers {

  /// <summary>Each helper's source and the helpers it calls.</summary>
  private static readonly Dictionary<string, (string Source, string[] Uses)> _helpers = new(System.StringComparer.Ordinal) {
    ["str_len"] = ("""
      fn @str_len(s: &str) -> usize {
        @str_byte(s, __units(s))
      }
      """, ["str_byte"]),
    ["str_byte"] = ("""
      fn @str_byte(s: &str, unit: usize) -> usize {
        let mut i = 0usize;
        let mut bytes = 0usize;
        while i < unit {
          let u = __unit(s, i);
          if u < 0x80 {
            bytes += 1;
          } else if u < 0x800 {
            bytes += 2;
          } else if u >= 0xD800 && u < 0xDC00 && i + 1 < unit {
            bytes += 4;
            i += 1;
          } else {
            bytes += 3;
          }
          i += 1;
        }
        bytes
      }
      """, []),
    ["str_unit"] = ("""
      fn @str_unit(s: &str, byte: usize) -> usize {
        let n = __units(s);
        let mut i = 0usize;
        let mut at = 0usize;
        while at < byte && i < n {
          let u = __unit(s, i);
          if u < 0x80 {
            at += 1;
          } else if u < 0x800 {
            at += 2;
          } else if u >= 0xD800 && u < 0xDC00 && i + 1 < n {
            at += 4;
            i += 1;
          } else {
            at += 3;
          }
          i += 1;
        }
        if at != byte {
          panic!("byte index is not a char boundary or is out of bounds");
        }
        i
      }
      """, []),
    ["str_slice"] = ("""
      fn @str_slice(s: &str, from: usize, to: usize) -> String {
        if from > to {
          panic!("begin <= end when slicing a string");
        }
        let a = @str_unit(s, from);
        let b = @str_unit(s, to);
        __substring(s, a, b)
      }
      """, ["str_unit"]),
    ["str_utf8"] = ("""
      fn @str_utf8(s: &str) -> Vec<u8> {
        let n = __units(s);
        let mut out: Vec<u8> = Vec::new();
        let mut i = 0usize;
        while i < n {
          let mut c = __unit(s, i);
          if c >= 0xD800 && c < 0xDC00 && i + 1 < n {
            c = 0x10000 + ((c - 0xD800) << 10) + (__unit(s, i + 1) - 0xDC00);
            i += 1;
          }
          if c < 0x80 {
            out.push(c as u8);
          } else if c < 0x800 {
            out.push((0xC0 | (c >> 6)) as u8);
            out.push((0x80 | (c & 0x3F)) as u8);
          } else if c < 0x10000 {
            out.push((0xE0 | (c >> 12)) as u8);
            out.push((0x80 | ((c >> 6) & 0x3F)) as u8);
            out.push((0x80 | (c & 0x3F)) as u8);
          } else {
            out.push((0xF0 | (c >> 18)) as u8);
            out.push((0x80 | ((c >> 12) & 0x3F)) as u8);
            out.push((0x80 | ((c >> 6) & 0x3F)) as u8);
            out.push((0x80 | (c & 0x3F)) as u8);
          }
          i += 1;
        }
        out
      }
      """, []),
    ["str_chars"] = ("""
      fn @str_chars(s: &str) -> Vec<u32> {
        let n = __units(s);
        let mut out: Vec<u32> = Vec::new();
        let mut i = 0usize;
        while i < n {
          let mut c = __unit(s, i);
          if c >= 0xD800 && c < 0xDC00 && i + 1 < n {
            c = 0x10000 + ((c - 0xD800) << 10) + (__unit(s, i + 1) - 0xDC00);
            i += 1;
          }
          out.push(c);
          i += 1;
        }
        out
      }
      """, []),
    ["str_char_count"] = ("""
      fn @str_char_count(s: &str) -> usize {
        let n = __units(s);
        let mut count = 0usize;
        let mut i = 0usize;
        while i < n {
          let u = __unit(s, i);
          if !(u >= 0xDC00 && u < 0xE000) {
            count += 1;
          }
          i += 1;
        }
        count
      }
      """, []),
    ["char_str"] = ("""
      fn @char_str(c: u32) -> String {
        if c < 0x10000 {
          __from_unit(c)
        } else {
          let v = c - 0x10000;
          __from_unit(0xD800 + (v >> 10)) + &__from_unit(0xDC00 + (v & 0x3FF))
        }
      }
      """, []),
    ["str_cmp"] = ("""
      fn @unit_key(u: u32) -> u32 {
        if u >= 0xD800 && u < 0xE000 {
          u + 0x2000
        } else if u >= 0xE000 {
          u - 0x800
        } else {
          u
        }
      }

      fn @str_cmp(a: &str, b: &str) -> i32 {
        let n = __units(a);
        let m = __units(b);
        let mut i = 0usize;
        while i < n && i < m {
          let x = @unit_key(__unit(a, i));
          let y = @unit_key(__unit(b, i));
          if x != y {
            return if x < y { -1 } else { 1 };
          }
          i += 1;
        }
        if n < m {
          -1
        } else if n > m {
          1
        } else {
          0
        }
      }
      """, []),
    ["is_whitespace"] = ("""
      fn @is_whitespace(c: u32) -> bool {
        (c >= 9 && c <= 13) || c == 0x20 || c == 0x85 || c == 0xA0 || c == 0x1680 || (c >= 0x2000 && c <= 0x200A)
          || c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000
      }
      """, []),
    ["str_trim"] = ("""
      fn @str_trim(s: &str, start: bool, end: bool) -> String {
        let n = __units(s);
        let mut from = 0usize;
        let mut to = n;
        if start {
          while from < n && @is_whitespace(__unit(s, from)) {
            from += 1;
          }
        }
        if end {
          while to > from && @is_whitespace(__unit(s, to - 1)) {
            to -= 1;
          }
        }
        __substring(s, from, to)
      }
      """, ["is_whitespace"]),
    ["str_ascii_case"] = ("""
      fn @str_ascii_case(s: &str, upper: bool) -> String {
        let n = __units(s);
        let mut out = String::new();
        let mut i = 0usize;
        while i < n {
          let mut u = __unit(s, i);
          if upper && u >= 97 && u <= 122 {
            u -= 32;
          } else if !upper && u >= 65 && u <= 90 {
            u += 32;
          }
          out.push_str(&__from_unit(u));
          i += 1;
        }
        out
      }
      """, []),
    ["str_split"] = ("""
      fn @str_split(s: &str, separator: &str) -> Vec<String> {
        let mut parts: Vec<String> = Vec::new();
        let n = __units(s);
        let m = __units(separator);
        let mut from = 0usize;
        loop {
          let at = __index_of(s, separator, from);
          if at < 0 {
            parts.push(__substring(s, from, n));
            return parts;
          }
          parts.push(__substring(s, from, at as usize));
          from = at as usize + m;
        }
      }
      """, []),
    ["str_find"] = ("""
      fn @str_find(s: &str, needle: &str) -> Option<usize> {
        let at = __index_of(s, needle, 0);
        if at < 0 {
          None
        } else {
          Some(@str_byte(s, at as usize))
        }
      }
      """, ["str_byte"]),
    ["parse_digits"] = ("""
      fn @parse_digits(s: &str, signed: bool) -> Option<u64> {
        let n = __units(s);
        if n == 0 {
          return None;
        }
        let mut i = 0usize;
        let first = __unit(s, 0);
        if first == 43 || (signed && first == 45) {
          if n == 1 {
            return None;
          }
          i = 1;
        }
        let mut value = 0u64;
        while i < n {
          let d = __unit(s, i);
          if d < 48 || d > 57 {
            return None;
          }
          let (times, high) = value.overflowing_mul(10);
          let (sum, carry) = times.overflowing_add((d - 48) as u64);
          if high || carry {
            return None;
          }
          value = sum;
          i += 1;
        }
        Some(value)
      }
      """, []),
    ["char_digit"] = ("""
      fn @char_digit(c: u32, radix: u32) -> Option<u32> {
        if radix < 2 || radix > 36 {
          panic!("to_digit: invalid radix -- radix must be in the range 2 to 36 inclusive");
        }
        let d = if c >= 48 && c <= 57 {
          c - 48
        } else if c >= 97 && c <= 122 {
          c - 87
        } else if c >= 65 && c <= 90 {
          c - 55
        } else {
          99
        };
        if d < radix {
          Some(d)
        } else {
          None
        }
      }
      """, []),
    ["char_from_digit"] = ("""
      fn @char_from_digit(num: u32, radix: u32) -> Option<u32> {
        if radix < 2 || radix > 36 {
          panic!("from_digit: radix is too high (maximum 36)");
        }
        if num >= radix {
          None
        } else if num < 10 {
          Some(48 + num)
        } else {
          Some(87 + num)
        }
      }
      """, [])
  };

  /// <summary>The source of the helpers named and of every helper they call, with the prefix in their names.</summary>
  public static string Source(IEnumerable<string> names, string prefix) {
    var wanted = new SortedSet<string>(System.StringComparer.Ordinal);
    var pending = new Stack<string>(names);
    while (pending.Count > 0) {
      var name = pending.Pop();
      if (!wanted.Add(name))
        continue;

      foreach (var used in _helpers[name].Uses)
        pending.Push(used);
    }

    return string.Concat(wanted.Select(name => _helpers[name].Source.Replace("@", prefix, System.StringComparison.Ordinal) + "\n\n"));
  }

}
