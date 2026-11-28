using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Polypiler.Core.Frontends.Python.Ast;

/// <summary>
/// The tree as one line of text, as the oracle of the Python layer writes CPython's: what
/// <c>ast.dump(tree, include_attributes=True, show_empty=True)</c> shows, in a form both sides spell
/// alike. A node is <c>Class(field=value,…@line:col:endline:endcol)</c> with the columns in bytes, a
/// list <c>[a,b]</c>, a str a JSON string with every character outside printable ASCII escaped (or
/// <c>cp:[…]</c>, its code points, where it holds a surrogate), a float <c>float.hex()</c>, a complex
/// <c>complex(real,imag)</c> of two, bytes <c>b:</c> and their hexadecimal digits.
/// </summary>
internal static class PyDump {

  /// <summary>The text of a tree.</summary>
  public static string Canonical(PyNode node) {
    var text = new StringBuilder();
    Write(text, node);
    return text.ToString();
  }

  private static void Write(StringBuilder text, object? value) {
    switch (value) {
      case null:
        text.Append("None");
        break;
      case PyNode node:
        text.Append(node.Kind).Append('(');
        var first = true;
        foreach (var (name, field) in node.Fields) {
          if (name.StartsWith('#'))
            continue;

          if (!first)
            text.Append(',');

          first = false;
          text.Append(name).Append('=');
          if (node.Kind is "Constant" && name == "value" && node.Get("#codePoints") is List<int> points)
            text.Append("cp:[").Append(string.Join(",", points)).Append(']');
          else
            Write(text, field);
        }

        if (node.Range is { } range)
          text.Append('@').Append(range.Line).Append(':').Append(range.Offset).Append(':').Append(range.EndLine).Append(':').Append(range.EndOffset);

        text.Append(')');
        break;
      case string str:
        Json(text, str);
        break;
      case bool flag:
        text.Append(flag ? "True" : "False");
        break;
      case int number:
        text.Append(number.ToString(CultureInfo.InvariantCulture));
        break;
      case BigInteger integer:
        text.Append(integer.ToString(CultureInfo.InvariantCulture));
        break;
      case double number:
        text.Append(Hex(number));
        break;
      case PyComplex complex:
        text.Append("complex(").Append(Hex(complex.Real)).Append(',').Append(Hex(complex.Imaginary)).Append(')');
        break;
      case byte[] bytes:
        text.Append("b:").Append(Convert.ToHexStringLower(bytes));
        break;
      case PyEllipsis:
        text.Append("...");
        break;
      case IEnumerable<string> texts:
        text.Append('[');
        var firstText = true;
        foreach (var item in texts) {
          if (!firstText)
            text.Append(',');

          firstText = false;
          Json(text, item);
        }

        text.Append(']');
        break;
      default:
        // What is left is a list of nodes.
        text.Append('[');
        var firstItem = true;
        foreach (var item in (System.Collections.IEnumerable)value) {
          if (!firstItem)
            text.Append(',');

          firstItem = false;
          Write(text, item);
        }

        text.Append(']');
        break;
    }
  }

  /// <summary>A string as <c>json.dumps(text, ensure_ascii=True)</c> writes it.</summary>
  private static void Json(StringBuilder text, string value) {
    text.Append('"');
    foreach (var c in value) {
      switch (c) {
        case '"':
          text.Append("\\\"");
          break;
        case '\\':
          text.Append("\\\\");
          break;
        case '\n':
          text.Append("\\n");
          break;
        case '\r':
          text.Append("\\r");
          break;
        case '\t':
          text.Append("\\t");
          break;
        case '\b':
          text.Append("\\b");
          break;
        case '\f':
          text.Append("\\f");
          break;
        default:
          if (c < 0x20 || c > 0x7E)
            text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
          else
            text.Append(c);

          break;
      }
    }

    text.Append('"');
  }

  /// <summary>
  /// A float as <c>float.hex()</c> writes it, such as <c>0x1.8000000000000p+1</c>; the parser's floats are
  /// literals, never negative nor NaN.
  /// </summary>
  private static string Hex(double number) {
    if (double.IsInfinity(number))
      return "inf";

    var bits = BitConverter.DoubleToInt64Bits(number);
    var exponent = (int)((bits >> 52) & 0x7FF);
    var fraction = bits & 0xFFFFFFFFFFFFFL;
    if (exponent == 0 && fraction == 0)
      return "0x0.0p+0";

    return exponent == 0
      ? $"0x0.{fraction:x13}p-1022"
      : $"0x1.{fraction:x13}p{exponent - 1023:+0;-0}";
  }

}
