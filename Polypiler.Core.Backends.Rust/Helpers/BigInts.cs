// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Syntax;

namespace Polypiler.Core.Backends.Rust.Helpers;

/// <summary>
/// The arbitrary-precision integer of the IR (<c>IrBigInts</c>) on its Rust representation, which the
/// standard library does not have: <c>PlyBigInt</c>, a sign and the magnitude's 32-bit digits, least
/// significant first. The algorithms are converted from num-bigint 0.5.1 (MIT,
/// THIRD-PARTY-NOTICE.num-bigint.txt beside this file): the schoolbook multiplication of
/// <c>biguint/multiplication.rs</c>, Knuth's algorithm D of <c>biguint/division.rs</c>, the shifts of
/// <c>biguint/shift.rs</c> and the two's complement bit operations of <c>bigint/bits.rs</c>, on the
/// sign-magnitude form of <c>bigint.rs</c>. Division and remainder truncate as the IR's do, a right
/// shift rounds down, a conversion to a fixed width keeps the low bits or traps, the conversion to
/// <c>f64</c> rounds to nearest. Each helper is Rust source in rustfmt's layout, safe and free of any
/// item of the standard library newer than Rust 1.31, read into the syntax tree when a crate needs it,
/// with the helpers it calls.
/// </summary>
internal static class BigInts {

  /// <summary>The name of the type in the crate.</summary>
  public const string TypeName = "PlyBigInt";

  /// <summary>The path of this file in the repository, the origin of every helper of it.</summary>
  public const string Origin = "Polypiler.Core.Backends.Rust/Helpers/BigInts.cs";

  /// <summary>The line of a crate's header that names the code the type is converted from, as its licence asks of a copy.</summary>
  public const string Notice = "PlyBigInt and the ply_big functions are converted from num-bigint (https://github.com/rust-num/num-bigint), Copyright (c) 2014 The Rust Project Developers, under the MIT licence.";

  /// <summary>The helper the order of the type calls.</summary>
  public const string Compare = "ply_big_compare";

  /// <summary>The type: the struct and its order, which <c>==</c>, <c>&lt;</c> and the other comparisons use.</summary>
  private const string TypeSource = """
      /// An integer of any magnitude: its sign and the 32-bit digits of its magnitude, the least significant
      /// first and no zero digit at the top; zero has no digits and is not negative.
      #[derive(Clone, Debug, Default, PartialEq, Eq)]
      pub struct PlyBigInt {
          pub negative: bool,
          pub magnitude: Vec<u32>,
      }

      impl PartialOrd for PlyBigInt {
          fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
              Some(self.cmp(other))
          }
      }

      impl Ord for PlyBigInt {
          fn cmp(&self, other: &Self) -> std::cmp::Ordering {
              if self.negative != other.negative {
                  other.negative.cmp(&self.negative)
              } else if self.negative {
                  ply_big_compare(&other.magnitude, &self.magnitude)
              } else {
                  ply_big_compare(&self.magnitude, &other.magnitude)
              }
          }
      }
      """;

  /// <summary>Every helper by name: its source, and the helpers it calls.</summary>
  private static readonly Dictionary<string, (string Source, string[] Calls)> _helpers = new(StringComparer.Ordinal) {
    ["ply_big_compare"] = ("""
      fn ply_big_compare(a: &[u32], b: &[u32]) -> std::cmp::Ordering {
          a.len()
              .cmp(&b.len())
              .then_with(|| a.iter().rev().cmp(b.iter().rev()))
      }
      """, []),
    ["ply_big_make"] = ("""
      fn ply_big_make(negative: bool, digits: Vec<u32>) -> PlyBigInt {
          let mut magnitude = digits;
          while magnitude.last() == Some(&0) {
              magnitude.pop();
          }
          PlyBigInt {
              negative: negative && !magnitude.is_empty(),
              magnitude,
          }
      }
      """, []),
    ["ply_big"] = ("""
      fn ply_big(value: i128) -> PlyBigInt {
          let mut rest = if value < 0 {
              (value as u128).wrapping_neg()
          } else {
              value as u128
          };
          let mut magnitude = Vec::new();
          while rest != 0 {
              magnitude.push(rest as u32);
              rest >>= 32;
          }
          ply_big_make(value < 0, magnitude)
      }
      """, ["ply_big_make"]),
    ["ply_big_parse"] = ("""
      fn ply_big_parse(text: &str) -> PlyBigInt {
          let mut magnitude = Vec::new();
          for digit in text.trim_start_matches('-').bytes() {
              let mut carry = u64::from(digit) - 48;
              for place in magnitude.iter_mut() {
                  carry += u64::from(*place) * 10;
                  *place = carry as u32;
                  carry >>= 32;
              }
              if carry != 0 {
                  magnitude.push(carry as u32);
              }
          }
          ply_big_make(text.starts_with('-'), magnitude)
      }
      """, ["ply_big_make"]),
    ["ply_big_add_magnitudes"] = ("""
      fn ply_big_add_magnitudes(a: &[u32], b: &[u32]) -> Vec<u32> {
          if a.len() < b.len() {
              return ply_big_add_magnitudes(b, a);
          }
          let mut sum = a.to_vec();
          sum.push(0);
          let mut others = b.iter();
          let mut carry = 0;
          for place in sum.iter_mut() {
              carry += u64::from(*place) + u64::from(others.next().cloned().unwrap_or(0));
              *place = carry as u32;
              carry >>= 32;
          }
          sum
      }
      """, []),
    ["ply_big_sub_magnitudes"] = ("""
      fn ply_big_sub_magnitudes(a: &[u32], b: &[u32]) -> Vec<u32> {
          let mut difference = a.to_vec();
          let mut others = b.iter();
          let mut borrow = 0;
          for place in difference.iter_mut() {
              borrow += i64::from(*place) - i64::from(others.next().cloned().unwrap_or(0));
              *place = borrow as u32;
              borrow >>= 32;
          }
          difference
      }
      """, []),
    ["ply_big_add"] = ("""
      fn ply_big_add(a: &PlyBigInt, b: &PlyBigInt) -> PlyBigInt {
          if a.negative == b.negative {
              return ply_big_make(
                  a.negative,
                  ply_big_add_magnitudes(&a.magnitude, &b.magnitude),
              );
          }
          if ply_big_compare(&a.magnitude, &b.magnitude) == std::cmp::Ordering::Less {
              ply_big_make(
                  b.negative,
                  ply_big_sub_magnitudes(&b.magnitude, &a.magnitude),
              )
          } else {
              ply_big_make(
                  a.negative,
                  ply_big_sub_magnitudes(&a.magnitude, &b.magnitude),
              )
          }
      }
      """, ["ply_big_make", "ply_big_add_magnitudes", "ply_big_compare", "ply_big_sub_magnitudes"]),
    ["ply_big_neg"] = ("""
      fn ply_big_neg(a: &PlyBigInt) -> PlyBigInt {
          ply_big_make(!a.negative, a.magnitude.clone())
      }
      """, ["ply_big_make"]),
    ["ply_big_sub"] = ("""
      fn ply_big_sub(a: &PlyBigInt, b: &PlyBigInt) -> PlyBigInt {
          ply_big_add(a, &ply_big_neg(b))
      }
      """, ["ply_big_add", "ply_big_neg"]),
    ["ply_big_mul"] = ("""
      fn ply_big_mul(a: &PlyBigInt, b: &PlyBigInt) -> PlyBigInt {
          let mut product = vec![0; a.magnitude.len() + b.magnitude.len()];
          for (offset, &x) in a.magnitude.iter().enumerate() {
              let row = &mut product[offset..];
              let mut carry = 0;
              for (place, &y) in row.iter_mut().zip(&b.magnitude) {
                  carry += u64::from(*place) + u64::from(x) * u64::from(y);
                  *place = carry as u32;
                  carry >>= 32;
              }
              row[b.magnitude.len()] = carry as u32;
          }
          ply_big_make(a.negative != b.negative, product)
      }
      """, ["ply_big_make"]),
    ["ply_big_shift_up"] = ("""
      fn ply_big_shift_up(digits: &[u32], shift: u32) -> Vec<u32> {
          let mut shifted = Vec::with_capacity(digits.len() + 1);
          let mut carry = 0;
          for &digit in digits {
              let wide = u64::from(digit) << shift | carry;
              shifted.push(wide as u32);
              carry = wide >> 32;
          }
          shifted.push(carry as u32);
          shifted
      }
      """, []),
    ["ply_big_shift_down"] = ("""
      fn ply_big_shift_down(digits: &[u32], shift: u32) -> Vec<u32> {
          let mut shifted = vec![0; digits.len()];
          let mut higher = 0;
          for (place, &digit) in shifted.iter_mut().zip(digits).rev() {
              *place = ((u64::from(higher) << 32 | u64::from(digit)) >> shift) as u32;
              higher = digit;
          }
          shifted
      }
      """, []),
    ["ply_big_div_rem"] = ("""
      fn ply_big_div_rem(a: &[u32], b: &[u32]) -> (Vec<u32>, Vec<u32>) {
          if ply_big_compare(a, b) == std::cmp::Ordering::Less {
              return (Vec::new(), a.to_vec());
          }
          if b.len() == 1 {
              let divisor = u64::from(b[0]);
              let mut quotient = a.to_vec();
              let mut remainder = 0;
              for place in quotient.iter_mut().rev() {
                  let current = remainder << 32 | u64::from(*place);
                  *place = (current / divisor) as u32;
                  remainder = current % divisor;
              }
              return (quotient, vec![remainder as u32]);
          }
          let shift = b[b.len() - 1].leading_zeros();
          let mut divisor = ply_big_shift_up(b, shift);
          divisor.pop();
          let mut rest = ply_big_shift_up(a, shift);
          let length = divisor.len();
          let top = u64::from(divisor[length - 1]);
          let next = u64::from(divisor[length - 2]);
          let mut quotient = Vec::with_capacity(rest.len() - length);
          for start in (0..rest.len() - length).rev() {
              let high = u64::from(rest[start + length]);
              let low = u64::from(rest[start + length - 1]);
              let mut guess = if high < top {
                  (high << 32 | low) / top
              } else {
                  0xffff_ffff
              };
              let mut remainder = if high < top {
                  (high << 32 | low) % top
              } else {
                  high + low
              };
              while remainder <= 0xffff_ffff
                  && guess * next > (remainder << 32 | u64::from(rest[start + length - 2]))
              {
                  guess -= 1;
                  remainder += top;
              }
              let mut carry = 0;
              let mut borrow = 0;
              for (place, &digit) in rest[start..].iter_mut().zip(&divisor) {
                  carry += guess * u64::from(digit);
                  let difference = i64::from(*place) - i64::from(carry as u32) + borrow;
                  *place = difference as u32;
                  borrow = difference >> 32;
                  carry >>= 32;
              }
              let difference = i64::from(rest[start + length]) - carry as i64 + borrow;
              rest[start + length] = difference as u32;
              if difference < 0 {
                  guess -= 1;
                  let mut carry = 0;
                  for (place, &digit) in rest[start..].iter_mut().zip(&divisor) {
                      carry += u64::from(*place) + u64::from(digit);
                      *place = carry as u32;
                      carry >>= 32;
                  }
                  rest[start + length] = rest[start + length].wrapping_add(carry as u32);
              }
              quotient.push(guess as u32);
          }
          quotient.reverse();
          rest.truncate(length);
          (quotient, ply_big_shift_down(&rest, shift))
      }
      """, ["ply_big_compare", "ply_big_shift_up", "ply_big_shift_down"]),
    ["ply_big_div"] = ("""
      fn ply_big_div(a: &PlyBigInt, b: &PlyBigInt) -> PlyBigInt {
          if b.magnitude.is_empty() {
              panic!("DivideByZero");
          }
          let quotient = ply_big_div_rem(&a.magnitude, &b.magnitude).0;
          ply_big_make(a.negative != b.negative, quotient)
      }
      """, ["ply_big_div_rem", "ply_big_make"]),
    ["ply_big_rem"] = ("""
      fn ply_big_rem(a: &PlyBigInt, b: &PlyBigInt) -> PlyBigInt {
          if b.magnitude.is_empty() {
              panic!("DivideByZero");
          }
          let remainder = ply_big_div_rem(&a.magnitude, &b.magnitude).1;
          ply_big_make(a.negative, remainder)
      }
      """, ["ply_big_div_rem", "ply_big_make"]),
    ["ply_big_drop_bits"] = ("""
      fn ply_big_drop_bits(digits: &[u32], count: u64) -> Vec<u32> {
          let whole = (count / 32) as usize;
          if whole >= digits.len() {
              return Vec::new();
          }
          ply_big_shift_down(&digits[whole..], (count % 32) as u32)
      }
      """, ["ply_big_shift_down"]),
    ["ply_big_shift"] = ("""
      fn ply_big_shift(a: &PlyBigInt, count: i64) -> PlyBigInt {
          if count >= 0 {
              let mut magnitude = vec![0; (count / 32) as usize];
              magnitude.extend(ply_big_shift_up(&a.magnitude, (count % 32) as u32));
              return ply_big_make(a.negative, magnitude);
          }
          let count = count.wrapping_neg() as u64;
          if a.negative {
              let rest = ply_big_drop_bits(&ply_big_sub_magnitudes(&a.magnitude, &[1]), count);
              ply_big_make(true, ply_big_add_magnitudes(&rest, &[1]))
          } else {
              ply_big_make(false, ply_big_drop_bits(&a.magnitude, count))
          }
      }
      """, ["ply_big_shift_up", "ply_big_make", "ply_big_drop_bits", "ply_big_sub_magnitudes", "ply_big_add_magnitudes"]),
    ["ply_big_negate_carry"] = ("""
      fn ply_big_negate_carry(digit: u32, carry: &mut u64) -> u32 {
          *carry += u64::from(!digit);
          let low = *carry as u32;
          *carry >>= 32;
          low
      }
      """, []),
    ["ply_big_twos"] = ("""
      fn ply_big_twos(value: &PlyBigInt, index: usize, carry: &mut u64) -> u32 {
          let digit = value.magnitude.get(index).cloned().unwrap_or(0);
          if value.negative {
              ply_big_negate_carry(digit, carry)
          } else {
              digit
          }
      }
      """, ["ply_big_negate_carry"]),
    ["ply_big_bitwise"] = ("""
      fn ply_big_bitwise(a: &PlyBigInt, b: &PlyBigInt, op: fn(u32, u32) -> u32) -> PlyBigInt {
          let fill = |value: &PlyBigInt| if value.negative { !0 } else { 0 };
          let negative = op(fill(a), fill(b)) != 0;
          let length = a.magnitude.len().max(b.magnitude.len()) + 1;
          let mut carry_a = 1;
          let mut carry_b = 1;
          let mut carry = 1;
          let mut digits = Vec::with_capacity(length);
          for index in 0..length {
              let digit = op(
                  ply_big_twos(a, index, &mut carry_a),
                  ply_big_twos(b, index, &mut carry_b),
              );
              digits.push(if negative {
                  ply_big_negate_carry(digit, &mut carry)
              } else {
                  digit
              });
          }
          ply_big_make(negative, digits)
      }
      """, ["ply_big_twos", "ply_big_negate_carry", "ply_big_make"]),
    ["ply_big_and"] = ("""
      fn ply_big_and(a: &PlyBigInt, b: &PlyBigInt) -> PlyBigInt {
          ply_big_bitwise(a, b, |x, y| x & y)
      }
      """, ["ply_big_bitwise"]),
    ["ply_big_or"] = ("""
      fn ply_big_or(a: &PlyBigInt, b: &PlyBigInt) -> PlyBigInt {
          ply_big_bitwise(a, b, |x, y| x | y)
      }
      """, ["ply_big_bitwise"]),
    ["ply_big_xor"] = ("""
      fn ply_big_xor(a: &PlyBigInt, b: &PlyBigInt) -> PlyBigInt {
          ply_big_bitwise(a, b, |x, y| x ^ y)
      }
      """, ["ply_big_bitwise"]),
    ["ply_big_not"] = ("""
      fn ply_big_not(a: &PlyBigInt) -> PlyBigInt {
          ply_big_sub(&ply_big_neg(a), &ply_big(1))
      }
      """, ["ply_big_sub", "ply_big_neg", "ply_big"]),
    ["ply_big_low"] = ("""
      fn ply_big_low(value: &PlyBigInt) -> u64 {
          let low = value
              .magnitude
              .iter()
              .take(2)
              .rev()
              .fold(0, |sum, &digit| sum << 32 | u64::from(digit));
          if value.negative {
              low.wrapping_neg()
          } else {
              low
          }
      }
      """, []),
    ["ply_big_fit"] = ("""
      fn ply_big_fit(value: &PlyBigInt, low: i128, high: i128) -> i128 {
          if value.magnitude.len() > 3 {
              panic!("IntegerOverflow");
          }
          let size = value
              .magnitude
              .iter()
              .rev()
              .fold(0, |sum, &digit| sum << 32 | i128::from(digit));
          let number = if value.negative { -size } else { size };
          if number < low {
              panic!("IntegerOverflow");
          }
          if number > high {
              panic!("IntegerOverflow");
          }
          number
      }
      """, []),
    ["ply_big_to_f64"] = ("""
      fn ply_big_to_f64(value: &PlyBigInt) -> f64 {
          let bits = value.magnitude.last().map_or(0, |&top| {
              32 * value.magnitude.len() as u64 - u64::from(top.leading_zeros())
          });
          let cut = bits.saturating_sub(55);
          let whole = (cut / 32) as usize;
          let partial = value.magnitude.get(whole).cloned().unwrap_or(0) & ((1 << (cut % 32)) - 1);
          let sticky = partial != 0 || value.magnitude[..whole].iter().any(|&digit| digit != 0);
          let top = ply_big_drop_bits(&value.magnitude, cut);
          let kept = ply_big_low(&ply_big_make(false, top));
          let scale = f64::from_bits((1023 + cut.min(1024)) << 52);
          let magnitude = (kept | u64::from(sticky)) as f64 * scale;
          if value.negative {
              -magnitude
          } else {
              magnitude
          }
      }
      """, ["ply_big_drop_bits", "ply_big_low", "ply_big_make"]),
    ["ply_big_of_f64"] = ("""
      fn ply_big_of_f64(number: f64) -> PlyBigInt {
          if !number.is_finite() {
              panic!("IntegerOverflow");
          }
          let bits = number.trunc().to_bits();
          let exponent = (bits >> 52 & 0x7ff) as i64;
          if exponent == 0 {
              return PlyBigInt::default();
          }
          let mantissa = ply_big(i128::from(bits & 0xf_ffff_ffff_ffff | 1 << 52));
          let shifted = ply_big_shift(&mantissa, exponent - 1075);
          ply_big_make(number < 0.0, shifted.magnitude)
      }
      """, ["ply_big", "ply_big_shift", "ply_big_make"])
  };

  /// <summary>The names of the helpers.</summary>
  public static IReadOnlyCollection<string> Names => _helpers.Keys;

  /// <summary>The struct and its two order traits, as items of the module.</summary>
  public static IReadOnlyList<RsItem> TypeItems() => RustSyntaxReader.Read(TypeSource.Replace("\r", "", StringComparison.Ordinal) + "\n").Items;

  /// <summary>A helper as its function, and every helper it calls, each once, the helper first.</summary>
  /// <param name="name">The helper.</param>
  /// <returns>The functions.</returns>
  public static IReadOnlyList<RsFunction> With(string name) {
    var names = new List<string>();
    void Add(string helper) {
      if (names.Contains(helper))
        return;

      names.Add(helper);
      foreach (var call in _helpers[helper].Calls)
        Add(call);
    }

    Add(name);
    return [.. names.Select(helper => (RsFunction)RustSyntaxReader.Read(_helpers[helper].Source.Replace("\r", "", StringComparison.Ordinal) + "\n").Items[0])];
  }

}
