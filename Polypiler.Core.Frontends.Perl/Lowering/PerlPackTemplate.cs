using System;
using System.Collections.Generic;
using System.Globalization;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl.Lowering;

/// <summary>One parsed field of a Perl <c>pack</c>/<c>unpack</c> template.</summary>
/// <param name="Type">The IR integer type of the field, or <see cref="IrType.Void"/> for <c>x</c> padding.</param>
/// <param name="LittleEndian">Whether the field is little-endian (ignored for 1-byte fields and padding).</param>
/// <param name="Count">Repeat count, or <c>-1</c> for <c>*</c> (rest of buffer).</param>
/// <param name="IsPad">Whether this entry is a pad byte (<c>x</c>).</param>
internal sealed record PerlPackItem(IrType Type, bool LittleEndian, int Count, bool IsPad);

/// <summary>
/// Parses and validates the portable endian-explicit subset of Perl's <c>pack</c>/<c>unpack</c> templates
/// (<c>C c n N v V</c>, <c>s S l L q Q j J</c> with <c>&lt;</c> or <c>&gt;</c>, counts, <c>*</c>, <c>x</c>).
/// Host-endian letters and unsupported codes are rejected with <c>PLY1723</c>.
/// </summary>
internal static class PerlPackTemplate {

  public static IReadOnlyList<PerlPackItem> Parse(string template, out string? error) {
    error = null;
    var items = new List<PerlPackItem>();
    var i = 0;
    while (i < template.Length) {
      if (char.IsWhiteSpace(template[i])) {
        ++i;
        continue;
      }

      var code = template[i++];
      IrType type;
      bool little;
      var isPad = false;

      switch (code) {
        case 'C': type = IrType.U8; little = true; break;
        case 'c': type = IrType.I8; little = true; break;
        case 'v': type = IrType.U16; little = true; break;
        case 'n': type = IrType.U16; little = false; break;
        case 'V': type = IrType.U32; little = true; break;
        case 'N': type = IrType.U32; little = false; break;
        case 'x': type = IrType.Void; little = true; isPad = true; break;
        case 's' or 'S' or 'l' or 'L' or 'q' or 'Q' or 'j' or 'J':
          if (i >= template.Length || template[i] is not ('<' or '>')) {
            error = $"letter '{code}' depends on host byte order; specify '<' or '>'";
            return [];
          }
          little = template[i++] == '<';
          type = code switch {
            's' => IrType.I16,
            'S' => IrType.U16,
            'l' => IrType.I32,
            'L' => IrType.U32,
            'q' or 'j' => IrType.I64,
            _ => IrType.U64
          };
          break;
        default:
          error = $"template letter '{code}' is outside the portable numeric subset";
          return [];
      }

      var count = 1;
      if (i < template.Length && template[i] == '*') {
        count = -1;
        ++i;
      } else if (i < template.Length && char.IsAsciiDigit(template[i])) {
        var start = i;
        while (i < template.Length && char.IsAsciiDigit(template[i]))
          ++i;
        count = int.Parse(template[start..i], CultureInfo.InvariantCulture);
      }

      items.Add(new(type, little, count, isPad));
    }

    if (items.Count == 0) {
      error = "empty pack/unpack template";
      return [];
    }

    return items;
  }

}
