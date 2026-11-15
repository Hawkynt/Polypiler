using System;
using System.Globalization;
using System.Numerics;
using Polypiler.Core.Frontends.Python.Ast;

namespace Polypiler.Core.Frontends.Python.Model;

/// <summary>The values of the constants of the wire format: integers of any size, floats from <c>float.hex()</c>.</summary>
internal static class Constants {

  /// <summary>The value of an integer constant, also negated by a unary minus; <see langword="null"/> for anything else.</summary>
  public static BigInteger? Integer(PyNode node) {
    var negated = false;
    while (node.Is("UnaryOp") && node.Operator() is "USub" or "UAdd") {
      negated ^= node.Operator() == "USub";
      node = node.Node("operand")!;
    }

    if (!node.Is("Constant") || node.Text("kind") != "int")
      return null;

    var value = BigInteger.Parse(node.Text("value")!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    return negated ? -value : value;
  }

  /// <summary>
  /// The text of a str constant, exactly: from its code points where it holds a surrogate - a lone
  /// surrogate is one UTF-16 code unit of its own - otherwise its value.
  /// </summary>
  public static string Text(PyNode node) {
    var points = node.Numbers("codePoints");
    if (points.Count == 0)
      return node.Text("value")!;

    var text = new System.Text.StringBuilder();
    foreach (var point in points)
      text.Append(point is >= 0xD800 and <= 0xDFFF ? ((char)point).ToString() : char.ConvertFromUtf32(point));

    return text.ToString();
  }

  /// <summary>
  /// The number <c>float.hex()</c> writes, such as <c>-0x1.8000000000000p+1</c>, <c>inf</c> or <c>nan</c>.
  /// The digits are at most 53 significant bits, so the number they make is exact before it is scaled.
  /// </summary>
  public static double Float(string text) {
    var negative = text.StartsWith('-');
    var body = negative || text.StartsWith('+') ? text[1..] : text;
    double value;
    if (body == "inf") {
      value = double.PositiveInfinity;
    } else if (body == "nan") {
      value = double.NaN;
    } else {
      // 0x<digit>.<digits>p<exponent>
      var exponentAt = body.IndexOf('p', StringComparison.Ordinal);
      var digits = body[2..exponentAt];
      var point = digits.IndexOf('.', StringComparison.Ordinal);
      var fraction = point < 0 ? "" : digits[(point + 1)..];
      var whole = point < 0 ? digits : digits[..point];
      var mantissa = ulong.Parse(whole + fraction, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
      var exponent = int.Parse(body[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
      value = Math.ScaleB(mantissa, exponent - 4 * fraction.Length);
    }

    return negative ? -value : value;
  }

}
