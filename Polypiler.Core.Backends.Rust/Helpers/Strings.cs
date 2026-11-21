using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Syntax;

namespace Polypiler.Core.Backends.Rust.Helpers;

/// <summary>
/// The string operations of the IR on the Rust representation of a string: a string is its UTF-16
/// code units, a <c>Vec&lt;u16&gt;</c> owned and a <c>&amp;[u16]</c> borrowed, so that length, index,
/// order and every search are by code unit as the IR defines them (<c>StringOperations</c>). Rust's
/// <c>str</c> is UTF-8 and holds no lone surrogate; these functions work on the representation, for
/// which the IR has no weaker operation to write them with. Each is Rust source in rustfmt's layout,
/// read into the syntax tree when a crate needs it, with the functions it calls.
/// </summary>
internal static partial class Strings {

  /// <summary>Every helper by name: its source, and the helpers it calls.</summary>
  private static readonly Dictionary<string, (string Source, string[] Calls)> _helpers = new(StringComparer.Ordinal) {
    ["ply_utf16"] = ("""
      fn ply_utf16(text: &str) -> Vec<u16> {
          text.encode_utf16().collect()
      }
      """, []),
    ["ply_position"] = ("""
      fn ply_position(position: i32, size: usize) -> usize {
          let position = usize::try_from(position).expect("IndexOutOfRange");
          if position > size {
              panic!("IndexOutOfRange");
          }
          position
      }
      """, []),
    ["ply_index_of"] = ("""
      fn ply_index_of(text: &[u16], needle: &[u16], from: i32) -> i32 {
          let mut start = ply_position(from, text.len());
          while start + needle.len() <= text.len() {
              if text[start..].starts_with(needle) {
                  return start as i32;
              }
              start += 1;
          }
          -1
      }
      """, ["ply_position"]),
    ["ply_last_index_of"] = ("""
      fn ply_last_index_of(text: &[u16], needle: &[u16], from: i32) -> i32 {
          let from = ply_position(from, text.len());
          if needle.len() > text.len() {
              return -1;
          }
          let mut start = from.min(text.len() - needle.len()) as i32;
          while start >= 0 {
              if text[start as usize..].starts_with(needle) {
                  return start;
              }
              start -= 1;
          }
          -1
      }
      """, ["ply_position"]),
    ["ply_replace"] = ("""
      fn ply_replace(text: &[u16], needle: &[u16], replacement: &[u16]) -> Vec<u16> {
          if needle.is_empty() {
              panic!("InvalidOperation");
          }
          let mut result = Vec::new();
          let mut start = 0;
          while start < text.len() {
              if text[start..].starts_with(needle) {
                  result.extend_from_slice(replacement);
                  start += needle.len();
              } else {
                  result.push(text[start]);
                  start += 1;
              }
          }
          result
      }
      """, []),
    ["ply_replace_first"] = ("""
      fn ply_replace_first(text: &[u16], needle: &[u16], replacement: &[u16]) -> Vec<u16> {
          let at = ply_index_of(text, needle, 0);
          if at < 0 {
              return text.to_vec();
          }
          let at = at as usize;
          [&text[..at], replacement, &text[at + needle.len()..]].concat()
      }
      """, ["ply_index_of"]),
    ["ply_split"] = ("""
      fn ply_split(text: &[u16], separator: &[u16], limit: i32) -> Vec<Vec<u16>> {
          let mut parts = Vec::new();
          if separator.is_empty() {
              for &unit in text {
                  parts.push(vec![unit]);
              }
          } else {
              let mut start = 0;
              let mut at = ply_index_of(text, separator, 0);
              while at >= 0 {
                  parts.push(text[start..at as usize].to_vec());
                  start = at as usize + separator.len();
                  at = ply_index_of(text, separator, start as i32);
              }
              parts.push(text[start..].to_vec());
          }
          if limit >= 0 {
              parts.truncate(limit as usize);
          }
          parts
      }
      """, ["ply_index_of"]),
    ["ply_trim"] = ("""
      fn ply_trim(text: &[u16], start: bool, end: bool, white: &[u16]) -> Vec<u16> {
          let mut first = 0;
          let mut last = text.len();
          while start && first < last && white.contains(&text[first]) {
              first += 1;
          }
          while end && last > first && white.contains(&text[last - 1]) {
              last -= 1;
          }
          text[first..last].to_vec()
      }
      """, []),
    ["ply_pad"] = ("""
      fn ply_pad(text: &[u16], length: i32, filler: &[u16], at_start: bool) -> Vec<u16> {
          let wanted = usize::try_from(length).unwrap_or(0);
          if wanted <= text.len() || filler.is_empty() {
              return text.to_vec();
          }
          let mut padding = Vec::new();
          while padding.len() < wanted - text.len() {
              padding.extend_from_slice(filler);
          }
          padding.truncate(wanted - text.len());
          if at_start {
              [&padding[..], text].concat()
          } else {
              [text, &padding[..]].concat()
          }
      }
      """, []),
    ["ply_enum_text"] = ("""
      fn ply_enum_text(bits: u64, digits: String, values: &[u64], names: &[&str], flags: bool) -> Vec<u16> {
          let mut index = 0;
          while index < values.len() {
              if values[index] == bits {
                  return ply_utf16(names[index]);
              }
              index += 1;
          }
          if !flags || bits == 0 {
              return ply_utf16(&digits);
          }
          let mut rest = bits;
          let mut found: Vec<&str> = Vec::new();
          let mut index = values.len();
          while index > 0 && rest != 0 {
              index -= 1;
              let flag = values[index];
              if flag != 0 && (rest & flag) == flag {
                  rest &= !flag;
                  found.push(names[index]);
              }
          }
          if rest != 0 {
              return ply_utf16(&digits);
          }
          found.reverse();
          ply_utf16(&found.join(", "))
      }
      """, ["ply_utf16"]),
    ["ply_enum_parse"] = ("""
      fn ply_enum_parse(
          text: &[u16],
          values: &[u64],
          names: &[&str],
          ignore_case: bool,
          low: i128,
          high: i128,
          white: &[u16],
      ) -> u64 {
          let text = ply_trim(text, true, false, white);
          if text.is_empty() {
              panic!("UnknownName");
          }
          let first = text[0];
          if first == 43 || first == 45 || (48..=57).contains(&first) {
              let mut end = text.len();
              while end > 0 && (text[end - 1] == 32 || (9..=13).contains(&text[end - 1])) {
                  end -= 1;
              }
              let mut index = if first == 43 || first == 45 { 1 } else { 0 };
              let mut valid = index < end;
              let mut number: i128 = 0;
              while valid && index < end {
                  if (48..=57).contains(&text[index]) {
                      number = (number * 10 + i128::from(text[index] - 48)).min(1 << 100);
                  } else {
                      valid = false;
                  }
                  index += 1;
              }
              if valid {
                  let value = if first == 45 { -number } else { number };
                  if value < low || value > high {
                      panic!("IntegerOverflow");
                  }
                  return value as u64;
              }
          }
          let mut bits = 0;
          let mut start = 0;
          while start <= text.len() {
              let mut end = start;
              while end < text.len() && text[end] != 44 {
                  end += 1;
              }
              let name = ply_trim(&text[start..end], true, true, white);
              let mut index = 0;
              while index < names.len() && !ply_same_name(names[index], &name, ignore_case) {
                  index += 1;
              }
              if index == names.len() {
                  panic!("UnknownName");
              }
              bits |= values[index];
              start = end + 1;
          }
          bits
      }
      """, ["ply_trim", "ply_same_name"]),
    ["ply_same_name"] = ("""
      fn ply_same_name(name: &str, text: &[u16], ignore_case: bool) -> bool {
          let name: Vec<u16> = ply_utf16(name);
          if name.len() != text.len() {
              return false;
          }
          let mut index = 0;
          while index < name.len() {
              if name[index] != text[index]
                  && !(ignore_case && ply_upper(name[index]) == ply_upper(text[index]))
              {
                  return false;
              }
              index += 1;
          }
          true
      }
      """, ["ply_utf16", "ply_upper"]),
    ["ply_list_index"] = ("""
      fn ply_list_index(index: i32, count: usize) -> usize {
          if index < 0 || index as usize >= count {
              panic!("ArgumentOutOfRange");
          }
          index as usize
      }
      """, []),
    ["ply_list_insert_index"] = ("""
      fn ply_list_insert_index(index: i32, count: usize) -> usize {
          if index < 0 || index as usize > count {
              panic!("ArgumentOutOfRange");
          }
          index as usize
      }
      """, []),
    ["ply_slot_freed"] = ("""
      fn ply_slot_freed<T>(slots: &[Result<T, u32>]) -> Option<usize> {
          let newest = slots.iter().filter_map(|slot| slot.as_ref().err()).max();
          slots
              .iter()
              .position(|slot| newest.is_some() && slot.as_ref().err() == newest)
      }
      """, []),
    ["ply_slot_insert"] = ("""
      fn ply_slot_insert<T>(slots: &mut Vec<Result<T, u32>>, entry: T) {
          match ply_slot_freed(slots) {
              Some(at) => slots[at] = Ok(entry),
              None => slots.push(Ok(entry)),
          }
      }
      """, ["ply_slot_freed"]),
    ["ply_slot_stamp"] = ("""
      fn ply_slot_stamp<T>(slots: &[Result<T, u32>]) -> u32 {
          slots
              .iter()
              .filter_map(|slot| slot.as_ref().err())
              .max()
              .map_or(0, |stamp| stamp + 1)
      }
      """, []),
    ["ply_map_find"] = ("""
      fn ply_map_find<K: PartialEq, V>(map: &[Result<(K, V), u32>], key: &K) -> Option<usize> {
          map.iter()
              .position(|slot| slot.as_ref().ok().map(|entry| &entry.0) == Some(key))
      }
      """, []),
    ["ply_map_get"] = ("""
      fn ply_map_get<K: PartialEq, V: Clone>(map: &[Result<(K, V), u32>], key: &K) -> Option<V> {
          ply_map_find(map, key)
              .and_then(|at| map[at].as_ref().ok())
              .map(|entry| entry.1.clone())
      }
      """, ["ply_map_find"]),
    ["ply_map_put"] = ("""
      fn ply_map_put<K: PartialEq, V>(map: &mut Vec<Result<(K, V), u32>>, key: K, value: V) {
          let found = ply_map_find(map, &key);
          match found {
              Some(at) => map[at] = Ok((key, value)),
              None => ply_slot_insert(map, (key, value)),
          }
      }
      """, ["ply_map_find", "ply_slot_insert"]),
    ["ply_map_add"] = ("""
      fn ply_map_add<K: PartialEq, V>(map: &mut Vec<Result<(K, V), u32>>, key: K, value: V) {
          if ply_map_find(map, &key).is_some() {
              panic!("DuplicateKey");
          }
          ply_slot_insert(map, (key, value));
      }
      """, ["ply_map_find", "ply_slot_insert"]),
    ["ply_map_try_add"] = ("""
      fn ply_map_try_add<K: PartialEq, V>(map: &mut Vec<Result<(K, V), u32>>, key: K, value: V) -> bool {
          if ply_map_find(map, &key).is_some() {
              return false;
          }
          ply_slot_insert(map, (key, value));
          true
      }
      """, ["ply_map_find", "ply_slot_insert"]),
    ["ply_map_remove"] = ("""
      fn ply_map_remove<K: PartialEq, V>(map: &mut [Result<(K, V), u32>], key: &K) -> bool {
          let found = ply_map_find(map, key);
          match found {
              Some(at) => {
                  let stamp = ply_slot_stamp(map);
                  map[at] = Err(stamp);
                  true
              }
              None => false,
          }
      }
      """, ["ply_map_find", "ply_slot_stamp"]),
    ["ply_set_find"] = ("""
      fn ply_set_find<T: PartialEq>(set: &[Result<T, u32>], element: &T) -> Option<usize> {
          set.iter().position(|slot| slot.as_ref().ok() == Some(element))
      }
      """, []),
    ["ply_set_add"] = ("""
      fn ply_set_add<T: PartialEq>(set: &mut Vec<Result<T, u32>>, element: T) -> bool {
          if ply_set_find(set, &element).is_some() {
              return false;
          }
          ply_slot_insert(set, element);
          true
      }
      """, ["ply_set_find", "ply_slot_insert"]),
    ["ply_set_remove"] = ("""
      fn ply_set_remove<T: PartialEq>(set: &mut [Result<T, u32>], element: &T) -> bool {
          let found = ply_set_find(set, element);
          match found {
              Some(at) => {
                  let stamp = ply_slot_stamp(set);
                  set[at] = Err(stamp);
                  true
              }
              None => false,
          }
      }
      """, ["ply_set_find", "ply_slot_stamp"]),
    ["ply_error"] = ("""
      fn ply_error(class: usize, message: Vec<u16>, inner: Option<Rc<PlyError>>) -> Rc<PlyError> {
          Rc::new(PlyError {
              class,
              message,
              inner,
          })
      }
      """, []),
    ["ply_is"] = ("""
      fn ply_is(class: usize, target: usize) -> bool {
          let mut at = class;
          while at != target && at != PLY_ERROR_BASES[at] {
              at = PLY_ERROR_BASES[at];
          }
          at == target
      }
      """, []),
    ["ply_unhandled"] = ("""
      fn ply_unhandled(error: &PlyError) -> ! {
          panic!("{}", PLY_ERROR_NAMES[error.class])
      }
      """, []),
    ["ply_with"] = ("""
      fn ply_with<T, R>(cell: &Mutex<T>, access: impl FnOnce(&mut T) -> R) -> R {
          let mut guard = cell.lock().unwrap_or_else(PoisonError::into_inner);
          access(&mut guard)
      }
      """, []),
    ["ply_value"] = ("""
      fn ply_value<T: Copy>(value: &Option<T>) -> T {
          value.expect("MissingValue")
      }
      """, []),
    ["ply_non_null"] = ("""
      fn ply_non_null<T>(value: &Option<T>) -> &T {
          value.as_ref().expect("NullReference")
      }
      """, []),
    ["ply_cast"] = ("""
      fn ply_cast<T: ?Sized, U: ?Sized>(value: Option<Rc<T>>, cast: fn(Rc<T>) -> Option<Rc<U>>) -> Option<Rc<U>> {
          match value.map(cast) {
              Some(None) => panic!("InvalidCast"),
              cast => cast.flatten(),
          }
      }
      """, []),
    ["ply_same_value_f64"] = ("""
      fn ply_same_value_f64(a: f64, b: f64) -> bool {
          a == b || (a.is_nan() && b.is_nan())
      }
      """, []),
    ["ply_same_value_f32"] = ("""
      fn ply_same_value_f32(a: f32, b: f32) -> bool {
          a == b || (a.is_nan() && b.is_nan())
      }
      """, []),
    ["ply_compare_f64"] = ("""
      fn ply_compare_f64(a: f64, b: f64) -> i32 {
          if a < b {
              -1
          } else if a > b {
              1
          } else if a == b || (a.is_nan() && b.is_nan()) {
              0
          } else if a.is_nan() {
              -1
          } else {
              1
          }
      }
      """, []),
    ["ply_compare_f32"] = ("""
      fn ply_compare_f32(a: f32, b: f32) -> i32 {
          if a < b {
              -1
          } else if a > b {
              1
          } else if a == b || (a.is_nan() && b.is_nan()) {
              0
          } else if a.is_nan() {
              -1
          } else {
              1
          }
      }
      """, []),
    ["ply_view"] = ("""
      fn ply_view<T>(source: &[T], start: i32, length: i32) -> &[T] {
          if start < 0 || length < 0 || start as usize + length as usize > source.len() {
              panic!("ArgumentOutOfRange");
          }
          &source[start as usize..start as usize + length as usize]
      }
      """, []),
    ["ply_view_from"] = ("""
      fn ply_view_from<T>(source: &[T], start: i32) -> &[T] {
          if start < 0 || start as usize > source.len() {
              panic!("ArgumentOutOfRange");
          }
          &source[start as usize..]
      }
      """, []),
    ["ply_view_mut"] = ("""
      fn ply_view_mut<T>(source: &mut [T], start: i32, length: i32) -> &mut [T] {
          if start < 0 || length < 0 || start as usize + length as usize > source.len() {
              panic!("ArgumentOutOfRange");
          }
          &mut source[start as usize..start as usize + length as usize]
      }
      """, []),
    ["ply_view_from_mut"] = ("""
      fn ply_view_from_mut<T>(source: &mut [T], start: i32) -> &mut [T] {
          if start < 0 || start as usize > source.len() {
              panic!("ArgumentOutOfRange");
          }
          &mut source[start as usize..]
      }
      """, []),
    ["ply_layout"] = ("""
      fn ply_layout(scientific: &str, negative: bool, ecma: bool, high: i32) -> Vec<u16> {
          let at = scientific.find('e').unwrap_or(scientific.len());
          let exponent: i32 = scientific[at + 1..].parse().unwrap_or(0);
          let digits = scientific[..at].replace('.', "");
          let k = digits.len() as i32;
          let n = exponent + 1;
          let mut text = String::new();
          if negative {
              text.push('-');
          }
          if (ecma && n <= 21 && n > -6) || (!ecma && exponent > -5 && exponent < high) {
              if k <= n {
                  text.push_str(&digits);
                  text.push_str(&"0".repeat((n - k) as usize));
              } else if n > 0 {
                  text.push_str(&digits[..n as usize]);
                  text.push('.');
                  text.push_str(&digits[n as usize..]);
              } else {
                  text.push_str("0.");
                  text.push_str(&"0".repeat(-n as usize));
                  text.push_str(&digits);
              }
          } else {
              text.push_str(&digits[..1]);
              if k > 1 {
                  text.push('.');
                  text.push_str(&digits[1..]);
              }
              let sign = if exponent < 0 { "-" } else { "+" };
              if ecma {
                  text.push_str(&format!("e{}{}", sign, exponent.abs()));
              } else {
                  text.push_str(&format!("E{}{:02}", sign, exponent.abs()));
              }
          }
          ply_utf16(&text)
      }
      """, ["ply_utf16"]),
    ["ply_format_f64"] = ("""
      fn ply_format_f64(x: f64, ecma: bool) -> Vec<u16> {
          if x.is_nan() {
              return ply_utf16("NaN");
          }
          if x.is_infinite() {
              return ply_utf16(if x < 0.0 { "-Infinity" } else { "Infinity" });
          }
          if x == 0.0 {
              return ply_utf16(if !ecma && x.is_sign_negative() { "-0" } else { "0" });
          }
          ply_layout(&format!("{:e}", x.abs()), x < 0.0, ecma, 17)
      }
      """, ["ply_utf16", "ply_layout"]),
    ["ply_format_f32"] = ("""
      fn ply_format_f32(x: f32) -> Vec<u16> {
          if x.is_nan() {
              return ply_utf16("NaN");
          }
          if x.is_infinite() {
              return ply_utf16(if x < 0.0 { "-Infinity" } else { "Infinity" });
          }
          if x == 0.0 {
              return ply_utf16(if x.is_sign_negative() { "-0" } else { "0" });
          }
          ply_layout(&ply_even_f32(x.abs()), x < 0.0, false, 9)
      }
      """, ["ply_utf16", "ply_layout", "ply_even_f32"]),
    ["ply_even_f32"] = ("""
      fn ply_even_f32(x: f32) -> String {
          let text = format!("{:e}", x);
          let at = text.find('e').unwrap_or(text.len());
          let last = text[..at].chars().last().unwrap_or('0');
          if !"13579".contains(last) {
              return text;
          }
          let exact = f64::from(x);
          let distance = (text.parse().unwrap_or(f64::NAN) - exact).abs();
          let mut step = 0;
          while step < 2 {
              let mut other = text[..at - 1].to_string();
              other.push(char::from(if step == 0 { last as u8 - 1 } else { last as u8 + 1 }));
              other.push_str(&text[at..]);
              let near: f64 = other.parse().unwrap_or(f64::NAN);
              if other.parse() == Ok(x) && (near - exact).abs() == distance {
                  return other;
              }
              step += 1;
          }
          text
      }
      """, []),
    ["ply_fixed"] = ("""
      fn ply_fixed(x: f64, digits: usize) -> Vec<u16> {
          if x.is_infinite() {
              return ply_utf16(if x < 0.0 { "-Infinity" } else { "Infinity" });
          }
          ply_utf16(&format!("{:.*}", digits, x))
      }
      """, ["ply_utf16"]),
    ["ply_signed_digits"] = ("""
      fn ply_signed_digits(value: i128, digits: usize) -> Vec<u16> {
          if value < 0 {
              ply_utf16(&format!("-{:0width$}", value.unsigned_abs(), width = digits))
          } else {
              ply_utf16(&format!("{:0width$}", value, width = digits))
          }
      }
      """, ["ply_utf16"]),
    ["ply_parse_integer"] = ("""
      fn ply_parse_integer(text: &[u16], low: i128, high: i128) -> i128 {
          let mut start = 0;
          let mut end = text.len();
          while start < end && (text[start] == 32 || (9..=13).contains(&text[start])) {
              start += 1;
          }
          while end > start && (text[end - 1] == 32 || (9..=13).contains(&text[end - 1])) {
              end -= 1;
          }
          let negative = start < end && text[start] == 45;
          if start < end && (text[start] == 43 || text[start] == 45) {
              start += 1;
          }
          if start == end {
              panic!("InvalidFormat");
          }
          let mut number: i128 = 0;
          while start < end {
              if !(48..=57).contains(&text[start]) {
                  panic!("InvalidFormat");
              }
              number = (number * 10 + i128::from(text[start] - 48)).min(1 << 100);
              start += 1;
          }
          let value = if negative { -number } else { number };
          if value < low || value > high {
              panic!("IntegerOverflow");
          }
          value
      }
      """, []),
    ["ply_float_text"] = ("""
      fn ply_float_text(text: &[u16]) -> String {
          let mut index = 0;
          let mut end = text.len();
          while index < end && (text[index] == 32 || (9..=13).contains(&text[index])) {
              index += 1;
          }
          while end > index && (text[end - 1] == 32 || (9..=13).contains(&text[end - 1])) {
              end -= 1;
          }
          let mut result = String::new();
          if index < end && (text[index] == 43 || text[index] == 45) {
              result.push(char::from(text[index] as u8));
              index += 1;
          }
          let rest = String::from_utf16_lossy(&text[index..end]).to_ascii_lowercase();
          if rest == "infinity" {
              result.push_str("inf");
              return result;
          }
          if rest == "nan" {
              return rest;
          }
          let mut digits = 0;
          while index < end && ((48..=57).contains(&text[index]) || text[index] == 44) {
              if text[index] != 44 {
                  result.push(char::from(text[index] as u8));
                  digits += 1;
              }
              index += 1;
          }
          if index < end && text[index] == 46 {
              result.push('.');
              index += 1;
              while index < end && (48..=57).contains(&text[index]) {
                  result.push(char::from(text[index] as u8));
                  digits += 1;
                  index += 1;
              }
          }
          if digits == 0 {
              panic!("InvalidFormat");
          }
          if index < end && (text[index] == 101 || text[index] == 69) {
              result.push('e');
              index += 1;
              if index < end && (text[index] == 43 || text[index] == 45) {
                  result.push(char::from(text[index] as u8));
                  index += 1;
              }
              let first = index;
              while index < end && (48..=57).contains(&text[index]) {
                  result.push(char::from(text[index] as u8));
                  index += 1;
              }
              if index == first {
                  panic!("InvalidFormat");
              }
          }
          if index != end {
              panic!("InvalidFormat");
          }
          result
      }
      """, []),
    ["ply_parse_f64"] = ("""
      fn ply_parse_f64(text: &[u16]) -> f64 {
          ply_float_text(text).parse().unwrap_or(f64::NAN)
      }
      """, ["ply_float_text"]),
    ["ply_parse_f32"] = ("""
      fn ply_parse_f32(text: &[u16]) -> f32 {
          ply_float_text(text).parse().unwrap_or(f32::NAN)
      }
      """, ["ply_float_text"]),
    ["ply_upper"] = ("""
      fn ply_upper(unit: u16) -> u16 {
          if (97..=122).contains(&unit) {
              unit - 32
          } else {
              unit
          }
      }
      """, [])
  };

  /// <summary>The name of the table of the code units a white-space set holds, with the set's units.</summary>
  public static (string Name, IReadOnlyList<char> Units) Whitespace(Polypiler.Core.IR.WhitespaceSet set)
    => set == Polypiler.Core.IR.WhitespaceSet.Ecma
      ? ("PLY_WHITESPACE_ECMA", Polypiler.Core.IR.StringOperations.EcmaWhitespace)
      : ("PLY_WHITESPACE_DOTNET", Polypiler.Core.IR.StringOperations.DotNetWhitespace);

  /// <summary>The names of the helpers.</summary>
  public static IReadOnlyCollection<string> Names => _helpers.Keys;

  /// <summary>
  /// Whether a helper, or one it calls, can panic: it holds <c>panic!</c>, <c>expect</c> or
  /// <c>unwrap()</c>, which the trap policy <c>result</c> leaves no panic to. A name that is no helper
  /// cannot.
  /// </summary>
  /// <param name="name">The helper.</param>
  /// <param name="results">Whether the forms of the trap policy <c>result</c> are asked about (<see cref="ResultForms"/>).</param>
  /// <returns><see langword="true"/> when a call of it may panic.</returns>
  public static bool Panics(string name, bool results = false)
    => _helpers.ContainsKey(name) && (results ? _resultPanics : _panics).GetOrAdd(name, helper => With(helper, results).Any(function => RustSyntaxRewriter.DescendantsAndSelf(function).Any(node =>
      node is RsMacro { Name: "panic" } || node is RsMethodCall { Name: "expect" or "unwrap" })));

  /// <summary>Whether a helper panics, by name, as <see cref="Panics"/> found it.</summary>
  private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _panics = new(StringComparer.Ordinal);

  /// <summary>Whether a helper's form under the trap policy <c>result</c> panics, by name, as <see cref="Panics"/> found it.</summary>
  private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _resultPanics = new(StringComparer.Ordinal);

  /// <summary>A helper as its function, and every helper it calls, each once, the helper first.</summary>
  /// <param name="name">The helper.</param>
  /// <param name="results">Whether to take the forms of the trap policy <c>result</c> where a helper has one.</param>
  /// <returns>The functions.</returns>
  public static IReadOnlyList<RsFunction> With(string name, bool results = false) {
    (string Source, string[] Calls) Of(string helper) => results && _resultForms.TryGetValue(helper, out var form) ? (form.Source, form.Calls) : _helpers[helper];
    var names = new List<string>();
    void Add(string helper) {
      if (names.Contains(helper))
        return;

      names.Add(helper);
      foreach (var call in Of(helper).Calls)
        Add(call);
    }

    Add(name);
    return [.. names.Select(helper => (RsFunction)RustSyntaxReader.Read(Of(helper).Source.Replace("\r", "", StringComparison.Ordinal) + "\n").Items[0])];
  }

}
