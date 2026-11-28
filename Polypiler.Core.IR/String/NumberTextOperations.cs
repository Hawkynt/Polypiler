using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Polypiler.Core.IR;

/// <summary>How <see cref="IrIntrinsicOp.FormatRadix"/> writes a negative number.</summary>
public enum RadixMode {

  /// <summary>The bits of the number at the width of its type, as .NET's <c>ToString("x8")</c> and <c>Convert.ToString(v, 2)</c> write them: <c>-1</c> of an <c>i32</c> in hexadecimal is <c>ffffffff</c>.</summary>
  TwoComplement,

  /// <summary>A minus sign in front of the digits of the magnitude, as JavaScript's <c>toString(radix)</c> and .NET's <c>"D&lt;n&gt;"</c> write it: <c>-1</c> in hexadecimal is <c>-1</c>.</summary>
  SignMagnitude

}

/// <summary>
/// The meaning of the conversions between numbers and text of the IR. Every language has its own rule
/// for each, and the rules are separate operations even where they look alike: .NET writes
/// <c>1E+21</c> and <c>-0</c>, JavaScript <c>1e+21</c> and <c>0</c>; <c>ToString("F0")</c> of 2.5 is
/// <c>2</c>, <c>toFixed(0)</c> is <c>3</c>; <c>double.Parse("1,000")</c> is 1000, <c>Number("1,000")</c>
/// is a NaN. A rule of .NET is the one of .NET Core 3.0 and later with the invariant culture, which the
/// interpreter runs on; a rule of ECMA-262 is written out here. The output of an operation depends on
/// its operands alone - on no culture and no runtime version.
/// </summary>
public static class NumberTextOperations {

  /// <summary>The largest number of digits <see cref="IrIntrinsicOp.FixedEcma"/> takes, as <c>toFixed</c> does.</summary>
  public const int MaxEcmaDigits = 100;

  /// <summary>The largest number of digits <see cref="IrIntrinsicOp.FixedDotNet"/> takes.</summary>
  public const int MaxDotNetDigits = 15;

  /// <summary>The largest number of digits <see cref="IrIntrinsicOp.FormatRadix"/> pads to.</summary>
  public const int MaxPadding = 99;

  /// <summary>The conversions between numbers and text of the IR.</summary>
  public static IReadOnlyCollection<IrIntrinsicOp> Operations { get; } = [
    IrIntrinsicOp.FormatEcma, IrIntrinsicOp.FormatDotNet, IrIntrinsicOp.FormatBoolEcma, IrIntrinsicOp.FormatBoolDotNet, IrIntrinsicOp.FormatRadix,
    IrIntrinsicOp.FixedEcma, IrIntrinsicOp.FixedDotNet, IrIntrinsicOp.ParseNumberEcma, IrIntrinsicOp.ParseFloatEcma, IrIntrinsicOp.ParseIntEcma,
    IrIntrinsicOp.ParseDotNet, IrIntrinsicOp.FormatVisualBasic
  ];

  private static readonly HashSet<IrIntrinsicOp> _operations = [.. Operations];

  /// <summary>Whether an operation is one of the conversions between numbers and text.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for the members of <see cref="Operations"/>.</returns>
  public static bool IsNumberText(IrIntrinsicOp op) => _operations.Contains(op);

  /// <summary>Whether a conversion takes the types, or <see langword="null"/> for an operation that is none.</summary>
  internal static bool? Accepts(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> arguments) {
    if (!IsNumberText(op))
      return null;

    bool Are(params Func<IrType, bool>[] rules) => arguments.Count == rules.Length && !rules.Where((rule, index) => !rule(arguments[index])).Any();
    var text = result == IrType.String;
    return op switch {
      IrIntrinsicOp.FormatEcma => text && Are(type => type == IrType.F64 || type.IsInteger),
      IrIntrinsicOp.FormatDotNet => text && Are(type => type.IsNumeric),
      IrIntrinsicOp.FormatBoolEcma or IrIntrinsicOp.FormatBoolDotNet => text && Are(type => type == IrType.Bool),
      IrIntrinsicOp.FormatRadix => text && Are(type => type.IsInteger, type => type == IrType.I32, type => type == IrType.I32, type => type == IrType.Bool, type => type == IrType.I32),
      IrIntrinsicOp.FixedEcma or IrIntrinsicOp.FixedDotNet => text && Are(type => type == IrType.F64, type => type == IrType.I32),
      IrIntrinsicOp.ParseNumberEcma or IrIntrinsicOp.ParseFloatEcma => result == IrType.F64 && Are(type => type.IsString),
      IrIntrinsicOp.ParseIntEcma => result == IrType.F64 && (Are(type => type.IsString) || Are(type => type.IsString, type => type == IrType.I32)),
      IrIntrinsicOp.FormatVisualBasic => text && Are(type => type.IsFloat, type => type == IrType.I32),
      _ => result.IsNumeric && Are(type => type.IsString)
    };
  }

  /// <summary>
  /// What is wrong with a well-typed node of a conversion beyond its types, or <see langword="null"/>:
  /// the digits of a fixed notation and the radix, the padding, the case and the mode of a radix are
  /// constants within their bounds.
  /// </summary>
  /// <param name="intrinsic">The node.</param>
  /// <returns>The problem, or <see langword="null"/>.</returns>
  public static string? Problem(IrIntrinsic intrinsic) {
    switch (intrinsic.Op) {
      case IrIntrinsicOp.FixedEcma or IrIntrinsicOp.FixedDotNet when intrinsic.Arguments.Count == 2:
        var most = intrinsic.Op == IrIntrinsicOp.FixedEcma ? MaxEcmaDigits : MaxDotNetDigits;
        return DigitsOf(intrinsic) is null ? $"the digits of {intrinsic.Op} are not a constant from 0 to {most}" : null;
      case IrIntrinsicOp.FormatVisualBasic when intrinsic.Arguments.Count == 2:
        return SeparatorOf(intrinsic) is null ? $"the separator of {intrinsic.Op} is not a constant code unit" : null;
      case IrIntrinsicOp.FormatRadix when intrinsic.Arguments.Count == 5:
        return RadixOf(intrinsic) is null
          ? $"the radix of {intrinsic.Op} is not a constant 2, 8 or 16 (two's complement) or 2 to 36 (sign and magnitude) with constant padding from 0 to {MaxPadding}, case and mode"
          : null;
      default:
        return null;
    }
  }

  /// <summary>The number of digits of a fixed notation, or <see langword="null"/> when it is not a constant within the bounds of its rule.</summary>
  /// <param name="intrinsic">A node of <see cref="IrIntrinsicOp.FixedEcma"/> or <see cref="IrIntrinsicOp.FixedDotNet"/>.</param>
  /// <returns>The digits.</returns>
  public static int? DigitsOf(IrIntrinsic intrinsic) {
    var most = intrinsic.Op == IrIntrinsicOp.FixedEcma ? MaxEcmaDigits : MaxDotNetDigits;
    return intrinsic.Arguments[1] is IrConst { Value: var digits } && digits >= 0 && digits <= most ? (int)digits : null;
  }

  /// <summary>The decimal separator of <see cref="IrIntrinsicOp.FormatVisualBasic"/>, or <see langword="null"/> when it is not a constant code unit other than 0.</summary>
  /// <param name="intrinsic">A node of <see cref="IrIntrinsicOp.FormatVisualBasic"/>.</param>
  /// <returns>The separator.</returns>
  public static char? SeparatorOf(IrIntrinsic intrinsic)
    => intrinsic.Arguments[1] is IrConst { Value: > 0 and <= char.MaxValue } separator ? (char)separator.Value : null;

  /// <summary>A node of <see cref="IrIntrinsicOp.FormatVisualBasic"/>.</summary>
  /// <param name="value">The number, an <c>f64</c> or an <c>f32</c>.</param>
  /// <param name="separator">The decimal separator.</param>
  /// <returns>The node.</returns>
  public static IrIntrinsic VisualBasic(IrExpr value, char separator) => new(IrIntrinsicOp.FormatVisualBasic, IrType.String, [value, new IrConst(IrType.I32, separator)]);

  /// <summary>The radix, the padding, the case and the mode of a radix, or <see langword="null"/> when they are not constants within their bounds.</summary>
  /// <param name="intrinsic">A node of <see cref="IrIntrinsicOp.FormatRadix"/>.</param>
  /// <returns>The four.</returns>
  public static (int Radix, int MinDigits, bool Upper, RadixMode Mode)? RadixOf(IrIntrinsic intrinsic) {
    if (intrinsic.Arguments[1] is not IrConst { Value: var radix } || intrinsic.Arguments[2] is not IrConst { Value: var padding and >= 0 and <= MaxPadding }
        || intrinsic.Arguments[3] is not IrConst { Value: var upper } || intrinsic.Arguments[4] is not IrConst { Value: (long)RadixMode.TwoComplement or (long)RadixMode.SignMagnitude } mode)
      return null;

    var valid = (RadixMode)mode.Value == RadixMode.TwoComplement ? radix is 2 or 8 or 16 : radix is >= 2 and <= 36;
    return valid ? ((int)radix, (int)padding, upper != 0, (RadixMode)mode.Value) : null;
  }

  /// <summary>A node of <see cref="IrIntrinsicOp.FormatRadix"/>.</summary>
  /// <param name="value">The integer.</param>
  /// <param name="radix">The radix.</param>
  /// <param name="minDigits">The least number of digits, padded with zeros.</param>
  /// <param name="upper">Whether the letters of the digits are upper case.</param>
  /// <param name="mode">How a negative number is written.</param>
  /// <returns>The node.</returns>
  public static IrIntrinsic Radix(IrExpr value, int radix, int minDigits, bool upper, RadixMode mode)
    => new(IrIntrinsicOp.FormatRadix, IrType.String, [value, new IrConst(IrType.I32, radix), new IrConst(IrType.I32, minDigits), new IrConst(IrType.Bool, upper ? 1 : 0), new IrConst(IrType.I32, (long)mode)]);

  /// <summary>A node of <see cref="IrIntrinsicOp.FixedEcma"/> or <see cref="IrIntrinsicOp.FixedDotNet"/>.</summary>
  /// <param name="op">The operation.</param>
  /// <param name="value">The number.</param>
  /// <param name="digits">The digits after the point.</param>
  /// <returns>The node.</returns>
  public static IrIntrinsic Fixed(IrIntrinsicOp op, IrExpr value, int digits) => new(op, IrType.String, [value, new IrConst(IrType.I32, digits)]);

  /// <summary>Evaluates a node of a well-typed module on the values of its arguments, as the interpreter carries them.</summary>
  internal static object Evaluate(IrIntrinsic intrinsic, object[] arguments) {
    var operand = intrinsic.Arguments[0].Type;
    switch (intrinsic.Op) {
      case IrIntrinsicOp.FormatEcma:
        return arguments[0] is double number ? FormatEcma(number) : IrOperators.Number(operand, (long)arguments[0]).ToString(CultureInfo.InvariantCulture);
      case IrIntrinsicOp.FormatDotNet:
        return arguments[0] switch {
          double real => FormatDotNet(real),
          float single => FormatDotNet(single),
          _ => IrOperators.Number(operand, (long)arguments[0]).ToString(CultureInfo.InvariantCulture)
        };
      case IrIntrinsicOp.FormatBoolEcma:
        return (bool)arguments[0] ? "true" : "false";
      case IrIntrinsicOp.FormatBoolDotNet:
        return (bool)arguments[0] ? "True" : "False";
      case IrIntrinsicOp.FormatRadix:
        var (radix, padding, upper, mode) = RadixOf(intrinsic)!.Value;
        return FormatRadix((long)arguments[0], operand, radix, padding, upper, mode);
      case IrIntrinsicOp.FixedEcma:
        return FixedEcma((double)arguments[0], (int)(long)arguments[1]);
      case IrIntrinsicOp.FixedDotNet:
        return FixedDotNet((double)arguments[0], (int)(long)arguments[1]);
      case IrIntrinsicOp.ParseNumberEcma:
        return ParseNumberEcma((string)arguments[0]);
      case IrIntrinsicOp.ParseFloatEcma:
        return ParseFloatEcma((string)arguments[0]);
      case IrIntrinsicOp.ParseIntEcma:
        return ParseIntEcma((string)arguments[0], arguments.Length == 1 ? 0 : (int)(long)arguments[1]);
      case IrIntrinsicOp.FormatVisualBasic:
        return arguments[0] is float binary32 ? FormatVisualBasic(binary32, SeparatorOf(intrinsic)!.Value) : FormatVisualBasic((double)arguments[0], SeparatorOf(intrinsic)!.Value);
      default:
        return ParseDotNet((string)arguments[0], intrinsic.Type);
    }
  }

  /// <summary>
  /// The shortest digits that read back as a finite number that is not zero, and the exponent of the
  /// first of them: the number is about <c>d.ddd × 10^exponent</c>. Of two such strings of digits the
  /// one nearer to the number; .NET Core 3.0 and later and ECMA-262 agree on them.
  /// </summary>
  /// <param name="value">The number, finite and not zero; its sign is ignored.</param>
  /// <returns>The digits, without leading or trailing zeros, and the exponent.</returns>
  public static (string Digits, int Exponent) Shortest(double value) {
    var round = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
    var at = round.IndexOf('E', StringComparison.Ordinal);
    var mantissa = at < 0 ? round : round[..at];
    var exponent = at < 0 ? 0 : int.Parse(round[(at + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    var point = mantissa.IndexOf('.', StringComparison.Ordinal);
    var whole = point < 0 ? mantissa.Length : point;
    var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
    var leading = digits.Length - digits.TrimStart('0').Length;
    digits = digits.Trim('0');
    return (digits, exponent + whole - 1 - leading);
  }

  /// <summary>
  /// ECMA-262's <c>Number::toString(x, 10)</c> (§6.1.6.1.20): the shortest digits; fixed notation while
  /// the exponent e of the first digit satisfies -7 &lt; e &lt; 21, otherwise <c>d.ddde±n</c>; -0 is
  /// <c>0</c>.
  /// </summary>
  /// <param name="value">The number.</param>
  /// <returns>The text.</returns>
  public static string FormatEcma(double value) {
    if (double.IsNaN(value))
      return "NaN";
    if (value == 0)
      return "0";
    if (double.IsInfinity(value))
      return value < 0 ? "-Infinity" : "Infinity";

    var (digits, exponent) = Shortest(value);
    var sign = value < 0 ? "-" : "";
    var k = digits.Length;
    var n = exponent + 1;
    if (k <= n && n <= 21)
      return sign + digits + new string('0', n - k);
    if (0 < n && n <= 21)
      return sign + digits[..n] + "." + digits[n..];
    if (-6 < n && n <= 0)
      return sign + "0." + new string('0', -n) + digits;

    var power = (exponent < 0 ? "e-" : "e+") + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
    return sign + digits[..1] + (k == 1 ? "" : "." + digits[1..]) + power;
  }

  /// <summary>.NET's <c>double.ToString(CultureInfo.InvariantCulture)</c> from .NET Core 3.0 on: the shortest digits; fixed notation while -5 &lt; e &lt; 15 + 2, otherwise <c>d.dddE±nn</c>; -0 is <c>-0</c>.</summary>
  /// <param name="value">The number.</param>
  /// <returns>The text.</returns>
  public static string FormatDotNet(double value) => value.ToString(CultureInfo.InvariantCulture);

  /// <summary>.NET's <c>float.ToString(CultureInfo.InvariantCulture)</c> from .NET Core 3.0 on: the shortest digits of binary32; fixed notation while -5 &lt; e &lt; 9.</summary>
  /// <param name="value">The number.</param>
  /// <returns>The text.</returns>
  public static string FormatDotNet(float value) => value.ToString(CultureInfo.InvariantCulture);

  /// <summary>Visual Basic's text of a <c>Double</c>: 15 significant digits (<see cref="IrIntrinsicOp.FormatVisualBasic"/>).</summary>
  /// <param name="value">The number.</param>
  /// <param name="separator">The decimal separator.</param>
  /// <returns>The text.</returns>
  public static string FormatVisualBasic(double value, char separator) => VisualBasicText(value, 15, separator);

  /// <summary>Visual Basic's text of a <c>Single</c>: 7 significant digits of its exact value.</summary>
  /// <param name="value">The number.</param>
  /// <param name="separator">The decimal separator.</param>
  /// <returns>The text.</returns>
  public static string FormatVisualBasic(float value, char separator) => VisualBasicText(value, 7, separator);

  /// <summary>
  /// The exact value of a number rounded to significant digits, a tie to the even digit, laid out as C's
  /// <c>%.&lt;n&gt;G</c> lays it out - the rule of oleaut32's <c>VarBstrFromR8</c> ([MS-VBAL] §5.5.1.2.4).
  /// </summary>
  private static string VisualBasicText(double value, int precision, char separator) {
    if (double.IsNaN(value))
      return "-1.#IND";
    if (double.IsInfinity(value))
      return value > 0 ? "1.#INF" : "-1.#INF";
    if (value == 0)
      return "0";

    var bits = BitConverter.DoubleToInt64Bits(value);
    var exponent = (int)((bits >> 52) & 0x7FF);
    var mantissa = (BigInteger)(bits & 0xFFFFFFFFFFFFFL);
    if (exponent == 0)
      exponent = 1;
    else
      mantissa += BigInteger.One << 52;

    // value = digits · 10^scale exactly: mantissa · 2^shift, and 2^-n = 5^n / 10^n.
    var shift = exponent - 1075;
    var digits = (shift >= 0 ? mantissa << shift : mantissa * BigInteger.Pow(5, -shift)).ToString(CultureInfo.InvariantCulture);
    var first = digits.Length - 1 + Math.Min(shift, 0);
    if (digits.Length > precision) {
      var kept = digits[..precision];
      var rest = digits[precision..];
      var up = rest[0] > '5' || (rest[0] == '5' && (rest.AsSpan(1).IndexOfAnyExcept('0') >= 0 || (kept[^1] - '0') % 2 == 1));
      digits = (BigInteger.Parse(kept, CultureInfo.InvariantCulture) + (up ? 1 : 0)).ToString(CultureInfo.InvariantCulture);
      if (digits.Length > precision) {
        digits = digits[..precision];
        ++first;
      }
    }

    digits = digits.TrimEnd('0');
    var sign = value < 0 ? "-" : "";
    var point = separator.ToString();
    if (first < -4 || first >= precision) {
      var power = (first < 0 ? "E-" : "E+") + Math.Abs(first).ToString("00", CultureInfo.InvariantCulture);
      return sign + digits[..1] + (digits.Length > 1 ? point + digits[1..] : "") + power;
    }

    if (first < 0)
      return sign + "0" + point + new string('0', -first - 1) + digits;
    return digits.Length <= first + 1 ? sign + digits + new string('0', first + 1 - digits.Length) : sign + digits[..(first + 1)] + point + digits[(first + 1)..];
  }

  /// <summary>The digits of an integer in a radix, padded with zeros to a number of digits.</summary>
  /// <param name="value">The integer as the IR carries it.</param>
  /// <param name="type">Its integer type.</param>
  /// <param name="radix">The radix, 2 to 36.</param>
  /// <param name="minDigits">The least number of digits.</param>
  /// <param name="upper">Whether the letters are upper case.</param>
  /// <param name="mode">How a negative number is written.</param>
  /// <returns>The text.</returns>
  public static string FormatRadix(long value, IrType type, int radix, int minDigits, bool upper, RadixMode mode) {
    var number = IrOperators.Number(type, value);
    var negative = mode == RadixMode.SignMagnitude && number < 0;
    // Two's complement: the bits of the value at the width of its type, read as unsigned.
    var magnitude = negative ? (UInt128)(-number) : mode == RadixMode.TwoComplement ? (UInt128)(ulong)value & ((UInt128.One << type.Bits) - 1) : (UInt128)number;
    var digits = new StringBuilder();
    do {
      digits.Insert(0, "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(magnitude % (uint)radix)]);
      magnitude /= (uint)radix;
    } while (magnitude != 0);

    var text = digits.ToString().PadLeft(minDigits, '0');
    return (negative ? "-" : "") + (upper ? text.ToUpperInvariant() : text);
  }

  /// <summary>
  /// ECMA-262's <c>Number.prototype.toFixed</c> (§21.1.3.3): the exact value rounded to the digits, a
  /// tie to the larger magnitude; a minus sign only for a number below zero, so -0 is <c>0.00</c>; from
  /// 10^21 on, and for a NaN, <see cref="FormatEcma(double)"/>.
  /// </summary>
  /// <param name="value">The number.</param>
  /// <param name="digits">The digits after the point, 0 to 100.</param>
  /// <returns>The text.</returns>
  public static string FixedEcma(double value, int digits) {
    if (double.IsNaN(value) || Math.Abs(value) >= 1e21)
      return FormatEcma(value);

    return (value < 0 ? "-" : "") + Fixed(Math.Abs(value), digits, true);
  }

  /// <summary>
  /// .NET's <c>double.ToString("F&lt;n&gt;", CultureInfo.InvariantCulture)</c> from .NET Core 3.0 on: the
  /// exact value rounded to the digits, a tie to the even digit, never an exponent; the sign of a
  /// negative number stays, also of -0 and of one that rounds to zero.
  /// </summary>
  /// <param name="value">The number.</param>
  /// <param name="digits">The digits after the point, 0 to 15.</param>
  /// <returns>The text.</returns>
  public static string FixedDotNet(double value, int digits) => value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

  /// <summary>The exact value of a finite number that is not negative, rounded to digits after the point.</summary>
  /// <param name="value">The number.</param>
  /// <param name="digits">The digits after the point.</param>
  /// <param name="tiesUp">Whether a tie goes to the larger magnitude, otherwise to the even digit.</param>
  /// <returns>The digits with the point.</returns>
  public static string Fixed(double value, int digits, bool tiesUp) {
    var bits = BitConverter.DoubleToInt64Bits(value) & long.MaxValue;
    var exponent = (int)(bits >> 52);
    var mantissa = (BigInteger)(bits & 0xFFFFFFFFFFFFFL);
    if (exponent == 0)
      exponent = 1;
    else
      mantissa += BigInteger.One << 52;

    // value = mantissa · 2^shift, and value · 10^digits rounded is the integer of the text.
    var shift = exponent - 1075;
    var scaled = mantissa * BigInteger.Pow(10, digits);
    BigInteger rounded;
    if (shift >= 0) {
      rounded = scaled << shift;
    } else {
      rounded = scaled >> -shift;
      var rest = scaled - (rounded << -shift);
      var half = BigInteger.One << (-shift - 1);
      if (rest > half || (rest == half && (tiesUp || !rounded.IsEven)))
        ++rounded;
    }

    var text = rounded.ToString(CultureInfo.InvariantCulture).PadLeft(digits + 1, '0');
    return digits == 0 ? text : text[..^digits] + "." + text[^digits..];
  }

  /// <summary>Whether a code unit is an ASCII digit of a radix.</summary>
  private static int Digit(char unit, int radix) {
    var value = unit switch {
      >= '0' and <= '9' => unit - '0',
      >= 'a' and <= 'z' => unit - 'a' + 10,
      >= 'A' and <= 'Z' => unit - 'A' + 10,
      _ => 99
    };
    return value < radix ? value : -1;
  }

  /// <summary>An integer as the nearest double, a tie to the even one.</summary>
  /// <param name="value">The integer, not negative.</param>
  /// <returns>The double; infinity beyond the largest one.</returns>
  public static double Nearest(BigInteger value) {
    var length = (int)value.GetBitLength();
    if (length <= 53)
      return (double)value;

    var shift = length - 54;
    var top = value >> shift;
    var sticky = !(value & ((BigInteger.One << shift) - 1)).IsZero;
    var mantissa = top >> 1;
    if (!top.IsEven && (sticky || !mantissa.IsEven))
      ++mantissa;

    return Math.ScaleB((double)mantissa, shift + 1);
  }

  /// <summary>The length of the longest prefix of a text from a position on that is a decimal literal of ECMA-262 without its sign: digits, a point, digits, an exponent.</summary>
  private static int DecimalLiteral(string text, int start) {
    var at = start;
    var digits = 0;
    while (at < text.Length && char.IsAsciiDigit(text[at])) {
      ++at;
      ++digits;
    }

    if (at < text.Length && text[at] == '.') {
      var point = at++;
      while (at < text.Length && char.IsAsciiDigit(text[at])) {
        ++at;
        ++digits;
      }

      // A point without a digit on either side is no number.
      if (digits == 0)
        return point - start;
    }

    if (digits == 0)
      return 0;

    if (at < text.Length && text[at] is 'e' or 'E') {
      var mark = at + 1;
      if (mark < text.Length && text[mark] is '+' or '-')
        ++mark;

      var first = mark;
      while (mark < text.Length && char.IsAsciiDigit(text[mark]))
        ++mark;

      if (mark > first)
        at = mark;
    }

    return at - start;
  }

  /// <summary>A decimal literal of ECMA-262 with an optional sign, correctly rounded.</summary>
  private static double Decimal(string literal) => double.Parse(literal, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);

  /// <summary>
  /// ECMA-262's <c>StringToNumber</c> (§7.1.4.1.1), which <c>Number(s)</c> and unary <c>+</c> apply: the
  /// text without the white space of ECMA-262 at both ends; empty it is 0; <c>0x</c>, <c>0o</c> and
  /// <c>0b</c> take the digits of their radix without a sign; otherwise a sign, then <c>Infinity</c> or
  /// a decimal literal; anything else is a NaN. The value is correctly rounded.
  /// </summary>
  /// <param name="text">The text.</param>
  /// <returns>The number.</returns>
  public static double ParseNumberEcma(string text) {
    var trimmed = StringOperations.Trim(text, WhitespaceSet.Ecma, true, true);
    if (trimmed.Length == 0)
      return 0;

    if (trimmed.Length > 2 && trimmed[0] == '0' && trimmed[1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B') {
      var radix = trimmed[1] is 'x' or 'X' ? 16 : trimmed[1] is 'o' or 'O' ? 8 : 2;
      var value = BigInteger.Zero;
      foreach (var unit in trimmed[2..]) {
        var digit = Digit(unit, radix);
        if (digit < 0)
          return double.NaN;

        value = value * radix + digit;
      }

      return Nearest(value);
    }

    var start = trimmed[0] is '+' or '-' ? 1 : 0;
    if (trimmed.AsSpan(start).SequenceEqual("Infinity"))
      return trimmed[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;

    return DecimalLiteral(trimmed, start) == trimmed.Length - start && trimmed.Length > start ? Decimal(trimmed) : double.NaN;
  }

  /// <summary>
  /// ECMA-262's <c>parseFloat</c> (§19.2.4): without the white space of ECMA-262 at the start, the
  /// longest prefix that is a sign and <c>Infinity</c> or a decimal literal; a NaN without one.
  /// </summary>
  /// <param name="text">The text.</param>
  /// <returns>The number.</returns>
  public static double ParseFloatEcma(string text) {
    var trimmed = StringOperations.Trim(text, WhitespaceSet.Ecma, true, false);
    var start = trimmed.Length > 0 && trimmed[0] is '+' or '-' ? 1 : 0;
    if (trimmed.AsSpan(start).StartsWith("Infinity", StringComparison.Ordinal))
      return trimmed[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;

    var length = DecimalLiteral(trimmed, start);
    return length == 0 ? double.NaN : Decimal(trimmed[..(start + length)]);
  }

  /// <summary>
  /// ECMA-262's <c>parseInt</c> (§19.2.5): without the white space of ECMA-262 at the start, a sign,
  /// then the longest run of digits of the radix - 0 means 10, or 16 after <c>0x</c>; 16 also drops a
  /// <c>0x</c> - as the nearest number; a NaN without a digit or for a radix outside 2 to 36.
  /// </summary>
  /// <param name="text">The text.</param>
  /// <param name="radix">The radix, or 0 for none.</param>
  /// <returns>The number.</returns>
  public static double ParseIntEcma(string text, int radix) {
    var trimmed = StringOperations.Trim(text, WhitespaceSet.Ecma, true, false);
    var negative = trimmed.Length > 0 && trimmed[0] == '-';
    var at = trimmed.Length > 0 && trimmed[0] is '+' or '-' ? 1 : 0;
    var strip = true;
    if (radix != 0) {
      if (radix < 2 || radix > 36)
        return double.NaN;

      strip = radix == 16;
    } else {
      radix = 10;
    }

    if (strip && trimmed.Length - at >= 2 && trimmed[at] == '0' && trimmed[at + 1] is 'x' or 'X') {
      at += 2;
      radix = 16;
    }

    var first = at;
    while (at < trimmed.Length && Digit(trimmed[at], radix) >= 0)
      ++at;

    if (at == first)
      return double.NaN;

    var number = radix is 2 or 4 or 8 or 10 or 16 or 32 ? Nearest(Integer(trimmed[first..at], radix)) : Approximated(trimmed[first..at].TrimStart('0'), radix);
    return negative ? -number : number;
  }

  /// <summary>Digits of a radix as the integer they are.</summary>
  private static BigInteger Integer(string digits, int radix) {
    var value = BigInteger.Zero;
    foreach (var unit in digits)
      value = value * radix + Digit(unit, radix);

    return value;
  }

  /// <summary>
  /// The digits of a radix other than 2, 4, 8, 10, 16 and 32, without leading zeros, as V8 approximates
  /// them, which ECMA-262 allows for these radices: in runs whose multiplier stays within 32 bits, each run added to the
  /// number so far times its multiplier, both in double arithmetic (<c>NumberParseIntHelper::HandleGenericCase</c>
  /// of <c>src/numbers/conversions.cc</c>, BSD-3-Clause).
  /// </summary>
  private static double Approximated(string digits, int radix) {
    const uint MaximumMultiplier = uint.MaxValue / 36;
    var result = 0.0;
    var at = 0;
    while (at < digits.Length) {
      uint part = 0;
      uint multiplier = 1;
      for (; at < digits.Length && multiplier * (uint)radix <= MaximumMultiplier; ++at) {
        part = part * (uint)radix + (uint)Digit(digits[at], radix);
        multiplier *= (uint)radix;
      }

      result = result * multiplier + part;
    }

    return result;
  }

  /// <summary>
  /// .NET's <c>Parse(s, CultureInfo.InvariantCulture)</c> of an integer type (<c>NumberStyles.Integer</c>)
  /// or of <c>double</c> and <c>float</c> (<c>NumberStyles.Float | NumberStyles.AllowThousands</c>), from
  /// .NET Core 3.0 on: correctly rounded, infinite beyond the range.
  /// </summary>
  /// <param name="text">The text.</param>
  /// <param name="type">The numeric type.</param>
  /// <returns>The number as the IR carries one of the type.</returns>
  /// <exception cref="IrTrapException"><see cref="IrTrap.InvalidFormat"/> for a malformed text, <see cref="IrTrap.IntegerOverflow"/> for an integer outside the type.</exception>
  public static object ParseDotNet(string text, IrType type) {
    var invariant = CultureInfo.InvariantCulture;
    const NumberStyles Floating = NumberStyles.Float | NumberStyles.AllowThousands;
    try {
      return type.Kind switch {
        IrTypeKind.F64 => double.Parse(text, Floating, invariant),
        IrTypeKind.F32 => float.Parse(text, Floating, invariant),
        IrTypeKind.I8 => (long)sbyte.Parse(text, NumberStyles.Integer, invariant),
        IrTypeKind.U8 => (long)byte.Parse(text, NumberStyles.Integer, invariant),
        IrTypeKind.I16 => (long)short.Parse(text, NumberStyles.Integer, invariant),
        IrTypeKind.U16 => (long)ushort.Parse(text, NumberStyles.Integer, invariant),
        IrTypeKind.I32 => (long)int.Parse(text, NumberStyles.Integer, invariant),
        IrTypeKind.U32 => (long)uint.Parse(text, NumberStyles.Integer, invariant),
        IrTypeKind.I64 => long.Parse(text, NumberStyles.Integer, invariant),
        _ => (object)(long)ulong.Parse(text, NumberStyles.Integer, invariant)
      };
    } catch (FormatException) {
      throw new IrTrapException(IrTrap.InvalidFormat);
    } catch (OverflowException) {
      throw new IrTrapException(IrTrap.IntegerOverflow);
    }
  }

}
