using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.Backends.JavaScript.Syntax;

namespace Polypiler.Backends.Testing;

/// <summary>
/// The structure of a target syntax tree as text, for comparing a printed tree with the tree its text
/// parses back to. Left out is what the text cannot say or says either way: the IR types, the layout
/// (blank lines, comments, one-line blocks, line breaks, tabulation, a literal's spelling), the helper
/// marks, the header, and the parentheses, whose effect is the structure itself. A minus sign in front
/// of a numeric literal is folded into it, and <c>NaN</c> and <c>Infinity</c> are numbers.
/// </summary>
internal static class SyntaxShape {

  private static readonly string[] _ignored = [
    "Type", "Raw", "Leading", "Inline", "Tabulated", "BreakBefore", "BreakBeforeQuestion", "BreakBeforeColon", "OpenBlank", "ElseInline",
    "Multiline", "IsHelper", "Header", "EqualityContract"
  ];

  /// <summary>The shape of a C# tree.</summary>
  public static string Of(CsNode node) => Dump(CSharpSyntaxRewriter.Rewrite(node, Normalized));

  /// <summary>The shape of a JavaScript tree.</summary>
  public static string Of(JsNode node) => Dump(JavaScriptSyntaxRewriter.Rewrite(node, Normalized));

  private static CsNode Normalized(CsNode node) => node switch {
    CsParenthesized parenthesized => parenthesized.Inner,
    CsPrefixUnary { Operator: "-", Operand: CsIntegerLiteral integer } => integer with { Value = -integer.Value },
    CsPrefixUnary { Operator: "-", Operand: CsRealLiteral real } => real with { Value = -real.Value },
    _ => node
  };

  private static JsNode Normalized(JsNode node) => node switch {
    JsParenthesized parenthesized => parenthesized.Inner,
    JsIdentifier { Name: "NaN" } => new JsNumber(double.NaN),
    JsIdentifier { Name: "Infinity" } => new JsNumber(double.PositiveInfinity),
    JsUnary { Operator: "-", Operand: JsNumber number } => number with { Value = -number.Value },
    JsUnary { Operator: "-", Operand: JsBigInt big } => big with { Value = -big.Value },
    _ => node
  };

  private static string Dump(object node) {
    var text = new StringBuilder();
    Dump(node, text);
    return text.ToString();
  }

  private static void Dump(object? value, StringBuilder text) {
    switch (value) {
      case null:
        text.Append("null");
        return;
      case string word:
        text.Append('"').Append(word).Append('"');
        return;
      case bool truth:
        text.Append(truth ? "true" : "false");
        return;
      case double number:
        text.Append(double.IsNaN(number) ? "NaN" : BitConverter.DoubleToInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture));
        return;
      case BigInteger or Enum:
        text.Append(value);
        return;
      case IEnumerable items:
        text.Append('[');
        foreach (var item in items) {
          Dump(item, text);
          text.Append(',');
        }

        text.Append(']');
        return;
    }

    var type = value.GetType();
    text.Append(type.Name).Append('(');
    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => !_ignored.Contains(property.Name)).OrderBy(property => property.Name, StringComparer.Ordinal)) {
      text.Append(property.Name).Append('=');
      Dump(property.GetValue(value), text);
      text.Append(' ');
    }

    text.Append(')');
  }

}
