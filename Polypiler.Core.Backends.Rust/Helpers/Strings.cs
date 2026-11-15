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
internal static class Strings {

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
      """, [])
  };

  /// <summary>The name of the table of the code units a white-space set holds, with the set's units.</summary>
  public static (string Name, IReadOnlyList<char> Units) Whitespace(Polypiler.Core.IR.WhitespaceSet set)
    => set == Polypiler.Core.IR.WhitespaceSet.Ecma
      ? ("PLY_WHITESPACE_ECMA", Polypiler.Core.IR.StringOperations.EcmaWhitespace)
      : ("PLY_WHITESPACE_DOTNET", Polypiler.Core.IR.StringOperations.DotNetWhitespace);

  /// <summary>The names of the helpers.</summary>
  public static IReadOnlyCollection<string> Names => _helpers.Keys;

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
