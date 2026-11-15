using System;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Polypiler.Core.Verification.Manifests;

/// <summary>
/// Writes a JSON tree as the manifest stores it: two-space indent, LF line ends, properties in the
/// order of the tree, <c>[]</c> and <c>{}</c> for what is empty, and strings and numbers serialized by
/// the rules of RFC 8785 (JSON Canonicalization Scheme, §3.2.2): a string escapes only <c>"</c>,
/// <c>\</c> and the control characters, the five with a short form using it and the rest
/// <c>\u00xx</c> in lower case; a number is the shortest text that reads back as the same
/// <see cref="double"/>, formatted as ECMAScript's <c>Number.prototype.toString</c> formats it. The
/// property order of RFC 8785 is not used: the manifest's own order is part of its schema.
/// </summary>
public static class CanonicalJson {

  /// <summary>The text of a tree, without a final line break.</summary>
  /// <param name="node">The tree; <see langword="null"/> is JSON <c>null</c>.</param>
  /// <returns>The text.</returns>
  /// <exception cref="ArgumentException">A number is not finite.</exception>
  public static string Serialize(JsonNode? node) {
    var text = new StringBuilder();
    Write(text, node, 0);
    return text.ToString();
  }

  private static void Write(StringBuilder text, JsonNode? node, int depth) {
    switch (node) {
      case null:
        text.Append("null");
        break;
      case JsonObject members:
        if (members.Count == 0) {
          text.Append("{}");
          break;
        }

        text.Append("{\n");
        var first = true;
        foreach (var (name, value) in members) {
          if (!first)
            text.Append(",\n");

          first = false;
          Indent(text, depth + 1);
          String(text, name);
          text.Append(": ");
          Write(text, value, depth + 1);
        }

        text.Append('\n');
        Indent(text, depth);
        text.Append('}');
        break;
      case JsonArray items:
        if (items.Count == 0) {
          text.Append("[]");
          break;
        }

        text.Append("[\n");
        for (var index = 0; index < items.Count; ++index) {
          if (index > 0)
            text.Append(",\n");

          Indent(text, depth + 1);
          Write(text, items[index], depth + 1);
        }

        text.Append('\n');
        Indent(text, depth);
        text.Append(']');
        break;
      default:
        Value(text, node.AsValue());
        break;
    }
  }

  private static void Value(StringBuilder text, JsonValue value) {
    // A JSON null is never a JsonValue: the tree holds it as a null node, which Write handles.
    switch (value.GetValueKind()) {
      case JsonValueKind.True:
        text.Append("true");
        break;
      case JsonValueKind.False:
        text.Append("false");
        break;
      case JsonValueKind.Number:
        // Whatever .NET type holds the number, its JSON text is the common ground.
        var literal = value.ToJsonString();
        text.Append(long.TryParse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
          ? integer.ToString(CultureInfo.InvariantCulture)
          : Number(double.Parse(literal, NumberStyles.Float, CultureInfo.InvariantCulture)));
        break;
      default:
        String(text, value.GetValue<string>());
        break;
    }
  }

  private static void Indent(StringBuilder text, int depth) => text.Append(' ', depth * 2);

  /// <summary>A string as RFC 8785 writes it; a lone surrogate is escaped, as ECMAScript's well-formed <c>JSON.stringify</c> does.</summary>
  private static void String(StringBuilder text, string value) {
    text.Append('"');
    for (var index = 0; index < value.Length; ++index) {
      var character = value[index];
      switch (character) {
        case '"':
          text.Append("\\\"");
          break;
        case '\\':
          text.Append("\\\\");
          break;
        case '\b':
          text.Append("\\b");
          break;
        case '\f':
          text.Append("\\f");
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
        default:
          var paired = char.IsHighSurrogate(character) ? index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])
            : char.IsLowSurrogate(character) ? index > 0 && char.IsHighSurrogate(value[index - 1])
            : true;
          if (character < ' ' || !paired)
            text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
          else
            text.Append(character);

          break;
      }
    }

    text.Append('"');
  }

  /// <summary>
  /// A finite number as ECMAScript's <c>Number::toString</c> (ECMA-262 §6.1.6.1.20) writes it, from the
  /// shortest digits that read back as the same value: plain notation for decimal exponents from -6 to
  /// 21, otherwise one digit, the rest after a point, and <c>e+</c> or <c>e-</c> with the exponent.
  /// </summary>
  /// <param name="value">The number.</param>
  /// <returns>The text.</returns>
  /// <exception cref="ArgumentException">The number is not finite.</exception>
  public static string Number(double value) {
    if (!double.IsFinite(value))
      throw new ArgumentException($"{value} has no JSON form", nameof(value));
    if (value == 0)
      return "0";

    var text = value.ToString("R", CultureInfo.InvariantCulture);
    var negative = text[0] == '-';
    if (negative)
      text = text[1..];

    var exponent = 0;
    var marker = text.IndexOf('E', StringComparison.Ordinal);
    if (marker >= 0) {
      exponent = int.Parse(text[(marker + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
      text = text[..marker];
    }

    var point = text.IndexOf('.', StringComparison.Ordinal);
    var whole = point < 0 ? text.Length : point;
    var digits = text.Replace(".", "", StringComparison.Ordinal);
    var trimmed = digits.TrimStart('0');
    var n = whole - (digits.Length - trimmed.Length) + exponent;
    digits = trimmed.TrimEnd('0');
    var k = digits.Length;
    var body = k <= n && n <= 21 ? digits + new string('0', n - k)
      : 0 < n && n <= 21 ? $"{digits[..n]}.{digits[n..]}"
      : -6 < n && n <= 0 ? $"0.{new string('0', -n)}{digits}"
      : $"{digits[..1]}{(k > 1 ? "." + digits[1..] : "")}e{(n - 1 < 0 ? '-' : '+')}{Math.Abs(n - 1).ToString(CultureInfo.InvariantCulture)}";
    return negative ? "-" + body : body;
  }

}
