using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>One value field of a <c>struct</c> format: its letter and where its bytes start.</summary>
/// <param name="Letter">The format character, such as <c>H</c>.</param>
/// <param name="Offset">The position of its first byte.</param>
internal sealed record StructField(char Letter, int Offset) {

  /// <summary>The number of bytes of the field in the standard sizes.</summary>
  public int Size => StructFormat.SizeOf(this.Letter);

  /// <summary>The Python type of the value: <c>int</c>, <c>float</c> or <c>bool</c>.</summary>
  public PyType Type => this.Letter switch {
    'f' or 'd' => PyType.Float,
    '?' => PyType.Bool,
    _ => PyType.Int
  };

  /// <summary>Whether the integer is signed.</summary>
  public bool Signed => char.IsLower(this.Letter);

}

/// <summary>
/// A <c>struct</c> format the import supports: a constant that starts with <c>&lt;</c>, <c>&gt;</c> or
/// <c>!</c> - the standard sizes, no alignment, an explicit byte order - followed by counts and the
/// letters <c>x B b H h I i L l Q q f d ?</c> (Library Reference, <c>struct</c>, "Format Strings").
/// The native forms, whose sizes and order are properties of the machine, are refused.
/// </summary>
internal sealed class StructFormat {

  private StructFormat(string text, bool little, IReadOnlyList<StructField> fields, int size) {
    this.Text = text;
    this.Little = little;
    this.Fields = fields;
    this.Size = size;
  }

  /// <summary>The format as the source writes it.</summary>
  public string Text { get; }

  /// <summary>Whether the lowest byte comes first.</summary>
  public bool Little { get; }

  /// <summary>The value fields, in order; padding has none.</summary>
  public IReadOnlyList<StructField> Fields { get; }

  /// <summary>The number of bytes: what <c>calcsize</c> gives.</summary>
  public int Size { get; }

  /// <summary>The type of what <c>unpack</c> gives: a tuple of the values.</summary>
  public PyType Type => PyType.Tuple([.. this.Fields.Select(item => item.Type)]);

  /// <summary>The number of bytes of a letter in the standard sizes.</summary>
  public static int SizeOf(char letter) => letter switch {
    'x' or 'B' or 'b' or '?' => 1,
    'H' or 'h' => 2,
    'I' or 'i' or 'L' or 'l' or 'f' => 4,
    _ => 8
  };

  /// <summary>The format a node holds, or why it is refused.</summary>
  public static (StructFormat? Format, string? Problem) Parse(PyNode node) {
    if (!(node.Is("Constant") && node.ConstantKind is "str" or "bytes"))
      return (null, "the format is not a constant");

    var text = node.ConstantKind == "str" ? node.Text("value")! : System.Text.Encoding.Latin1.GetString((byte[])node.Value!);
    return Parse(text);
  }

  /// <summary>A format from its text, or why it is refused.</summary>
  public static (StructFormat? Format, string? Problem) Parse(string text) {
    if (text.Length == 0 || text[0] is not ('<' or '>' or '!'))
      return (null, "only the formats with an explicit byte order '<', '>' or '!' and standard sizes are imported; the native byte order, size and alignment are properties of the machine");

    var fields = new List<StructField>();
    var offset = 0;
    var index = 1;
    while (index < text.Length) {
      if (char.IsWhiteSpace(text[index])) {
        ++index;
        continue;
      }

      var count = 1;
      if (char.IsAsciiDigit(text[index])) {
        var start = index;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
          ++index;

        if (!int.TryParse(text[start..index], out count) || count > 4096)
          return (null, "a count beyond 4096");

        if (index == text.Length)
          return (null, "a count without a letter, which raises struct.error");
      }

      var letter = text[index++];
      if ("xBbHhIiLlQqfd?".IndexOf(letter) < 0)
        return (null, $"the letter '{letter}' is not imported; supported are x B b H h I i L l Q q f d ?");

      for (var repeat = 0; repeat < count; ++repeat) {
        if (letter != 'x')
          fields.Add(new StructField(letter, offset));

        offset += SizeOf(letter);
      }
    }

    return (new StructFormat(text, text[0] == '<', fields, offset), null);
  }

}
