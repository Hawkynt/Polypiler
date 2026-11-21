using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.Rust.Helpers;

internal static partial class Strings {

  /// <summary>
  /// The forms of the helpers that trap under the trap policy <c>result</c>, which leaves no panic: a
  /// trap is the helper's value, never a panic. A helper of one trap returns an <c>Option</c>, <c>None</c>
  /// where it traps, which the call turns into the crate's error; a helper of two takes the error of each
  /// as an argument, generic over the error's type, and returns a <c>Result</c> of it, which the call
  /// hands on (<see cref="Traps"/>, in the order of the arguments). A helper that only calls such a
  /// helper where it cannot trap takes the value it gives in that case and traps no more. Every
  /// other helper has one form for both policies.
  /// </summary>
  private static readonly Dictionary<string, (string Source, string[] Calls, string[] Traps)> _resultForms = new(StringComparer.Ordinal) {
    ["ply_position"] = ("""
      fn ply_position(position: i32, size: usize) -> Option<usize> {
          let position = usize::try_from(position).ok()?;
          if position > size {
              return None;
          }
          Some(position)
      }
      """, [], ["IndexOutOfRange"]),
    ["ply_index_of"] = ("""
      fn ply_index_of(text: &[u16], needle: &[u16], from: i32) -> Option<i32> {
          let mut start = ply_position(from, text.len())?;
          while start + needle.len() <= text.len() {
              if text[start..].starts_with(needle) {
                  return Some(start as i32);
              }
              start += 1;
          }
          Some(-1)
      }
      """, ["ply_position"], ["IndexOutOfRange"]),
    ["ply_last_index_of"] = ("""
      fn ply_last_index_of(text: &[u16], needle: &[u16], from: i32) -> Option<i32> {
          let from = ply_position(from, text.len())?;
          if needle.len() > text.len() {
              return Some(-1);
          }
          let mut start = from.min(text.len() - needle.len()) as i32;
          while start >= 0 {
              if text[start as usize..].starts_with(needle) {
                  return Some(start);
              }
              start -= 1;
          }
          Some(-1)
      }
      """, ["ply_position"], ["IndexOutOfRange"]),
    ["ply_replace"] = ("""
      fn ply_replace(text: &[u16], needle: &[u16], replacement: &[u16]) -> Option<Vec<u16>> {
          if needle.is_empty() {
              return None;
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
          Some(result)
      }
      """, [], ["InvalidOperation"]),
    ["ply_replace_first"] = ("""
      fn ply_replace_first(text: &[u16], needle: &[u16], replacement: &[u16]) -> Vec<u16> {
          let at = ply_index_of(text, needle, 0).unwrap_or(-1);
          if at < 0 {
              return text.to_vec();
          }
          let at = at as usize;
          [&text[..at], replacement, &text[at + needle.len()..]].concat()
      }
      """, ["ply_index_of"], []),
    ["ply_split"] = ("""
      fn ply_split(text: &[u16], separator: &[u16], limit: i32) -> Vec<Vec<u16>> {
          let mut parts = Vec::new();
          if separator.is_empty() {
              for &unit in text {
                  parts.push(vec![unit]);
              }
          } else {
              let mut start = 0;
              let mut at = ply_index_of(text, separator, 0).unwrap_or(-1);
              while at >= 0 {
                  parts.push(text[start..at as usize].to_vec());
                  start = at as usize + separator.len();
                  at = ply_index_of(text, separator, start as i32).unwrap_or(-1);
              }
              parts.push(text[start..].to_vec());
          }
          if limit >= 0 {
              parts.truncate(limit as usize);
          }
          parts
      }
      """, ["ply_index_of"], []),
    ["ply_enum_parse"] = ("""
      fn ply_enum_parse<E>(
          text: &[u16],
          values: &[u64],
          names: &[&str],
          ignore_case: bool,
          low: i128,
          high: i128,
          white: &[u16],
          unknown: E,
          overflow: E,
      ) -> Result<u64, E> {
          let text = ply_trim(text, true, false, white);
          if text.is_empty() {
              return Err(unknown);
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
                      return Err(overflow);
                  }
                  return Ok(value as u64);
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
                  return Err(unknown);
              }
              bits |= values[index];
              start = end + 1;
          }
          Ok(bits)
      }
      """, ["ply_trim", "ply_same_name"], ["UnknownName", "IntegerOverflow"]),
    ["ply_list_index"] = ("""
      fn ply_list_index(index: i32, count: usize) -> Option<usize> {
          if index < 0 || index as usize >= count {
              return None;
          }
          Some(index as usize)
      }
      """, [], ["ArgumentOutOfRange"]),
    ["ply_list_insert_index"] = ("""
      fn ply_list_insert_index(index: i32, count: usize) -> Option<usize> {
          if index < 0 || index as usize > count {
              return None;
          }
          Some(index as usize)
      }
      """, [], ["ArgumentOutOfRange"]),
    ["ply_map_add"] = ("""
      fn ply_map_add<K: PartialEq, V>(map: &mut Vec<Result<(K, V), u32>>, key: K, value: V) -> Option<()> {
          if ply_map_find(map, &key).is_some() {
              return None;
          }
          ply_slot_insert(map, (key, value));
          Some(())
      }
      """, ["ply_map_find", "ply_slot_insert"], ["DuplicateKey"]),
    ["ply_cast"] = ("""
      fn ply_cast<T: ?Sized, U: ?Sized>(value: Option<Rc<T>>, cast: fn(Rc<T>) -> Option<Rc<U>>) -> Option<Option<Rc<U>>> {
          match value.map(cast) {
              Some(None) => None,
              cast => Some(cast.flatten()),
          }
      }
      """, [], ["InvalidCast"]),
    ["ply_view"] = ("""
      fn ply_view<T>(source: &[T], start: i32, length: i32) -> Option<&[T]> {
          if start < 0 || length < 0 || start as usize + length as usize > source.len() {
              return None;
          }
          Some(&source[start as usize..start as usize + length as usize])
      }
      """, [], ["ArgumentOutOfRange"]),
    ["ply_view_from"] = ("""
      fn ply_view_from<T>(source: &[T], start: i32) -> Option<&[T]> {
          if start < 0 || start as usize > source.len() {
              return None;
          }
          Some(&source[start as usize..])
      }
      """, [], ["ArgumentOutOfRange"]),
    ["ply_view_mut"] = ("""
      fn ply_view_mut<T>(source: &mut [T], start: i32, length: i32) -> Option<&mut [T]> {
          if start < 0 || length < 0 || start as usize + length as usize > source.len() {
              return None;
          }
          Some(&mut source[start as usize..start as usize + length as usize])
      }
      """, [], ["ArgumentOutOfRange"]),
    ["ply_view_from_mut"] = ("""
      fn ply_view_from_mut<T>(source: &mut [T], start: i32) -> Option<&mut [T]> {
          if start < 0 || start as usize > source.len() {
              return None;
          }
          Some(&mut source[start as usize..])
      }
      """, [], ["ArgumentOutOfRange"]),
    ["ply_parse_integer"] = ("""
      fn ply_parse_integer<E>(
          text: &[u16],
          low: i128,
          high: i128,
          invalid: E,
          overflow: E,
      ) -> Result<i128, E> {
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
              return Err(invalid);
          }
          let mut number: i128 = 0;
          while start < end {
              if !(48..=57).contains(&text[start]) {
                  return Err(invalid);
              }
              number = (number * 10 + i128::from(text[start] - 48)).min(1 << 100);
              start += 1;
          }
          let value = if negative { -number } else { number };
          if value < low || value > high {
              return Err(overflow);
          }
          Ok(value)
      }
      """, [], ["InvalidFormat", "IntegerOverflow"]),
    ["ply_float_text"] = ("""
      fn ply_float_text(text: &[u16]) -> Option<String> {
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
              return Some(result);
          }
          if rest == "nan" {
              return Some(rest);
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
              return None;
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
                  return None;
              }
          }
          if index != end {
              return None;
          }
          Some(result)
      }
      """, [], ["InvalidFormat"]),
    ["ply_parse_f64"] = ("""
      fn ply_parse_f64(text: &[u16]) -> Option<f64> {
          Some(ply_float_text(text)?.parse().unwrap_or(f64::NAN))
      }
      """, ["ply_float_text"], ["InvalidFormat"]),
    ["ply_parse_f32"] = ("""
      fn ply_parse_f32(text: &[u16]) -> Option<f32> {
          Some(ply_float_text(text)?.parse().unwrap_or(f32::NAN))
      }
      """, ["ply_float_text"], ["InvalidFormat"])
  };

  /// <summary>The helpers that have a form of their own under the trap policy <c>result</c>.</summary>
  public static IReadOnlyCollection<string> ResultForms => _resultForms.Keys;

  /// <summary>
  /// The traps the form of a helper under the trap policy <c>result</c> gives as its value, by their
  /// names in <c>IrTrap</c>: one for an <c>Option</c>; more for a <c>Result</c>, whose errors the call
  /// passes as the last arguments in this order; none for a helper that has no such form or never traps.
  /// </summary>
  /// <param name="name">The helper.</param>
  /// <returns>The names of the traps.</returns>
  public static IReadOnlyList<string> Traps(string name) => _resultForms.TryGetValue(name, out var form) ? form.Traps : [];

}
