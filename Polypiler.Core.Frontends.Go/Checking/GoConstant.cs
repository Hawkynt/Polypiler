using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Polypiler.Core.Frontends.Go.Checking;

/// <summary>The kind of a constant value, as <c>constant.Kind</c> has it (complex values are outside the frontend).</summary>
internal enum GoConstantKind {
  Bool,
  String,
  Int,
  Float
}

/// <summary>A value beyond the range the frontend computes constants in.</summary>
internal sealed class GoConstantRangeException(string message) : Exception(message);

/// <summary>
/// An exact constant value of Go (The Go Programming Language Specification, "Constants"), with the
/// representation and the operations of <c>go/constant</c> (BSD-3-Clause, see
/// <c>THIRD-PARTY-NOTICE.Go.txt</c>): booleans, strings of bytes, integers of any size, and
/// floating-point values that are exact fractions while numerator and denominator stay below 4096 bits
/// and binary floating-point numbers of 512 bits of mantissa beyond. <see cref="ExactString"/> writes a
/// value as <c>constant.Value.ExactString</c> does.
/// </summary>
internal sealed class GoConstant : IEquatable<GoConstant> {

  /// <summary>The bits of mantissa of a floating-point value that is no fraction.</summary>
  public const int WidePrecision = 512;

  /// <summary>The bit length below which the parts of a fraction keep it a fraction.</summary>
  public const int MaxExponent = 4 << 10;

  /// <summary>The largest binary exponent a literal or a value may have; beyond, the frontend refuses the constant.</summary>
  public const int ExponentLimit = 100_000;

  private GoConstant(GoConstantKind kind) => this.Kind = kind;

  /// <summary>The kind of the value.</summary>
  public GoConstantKind Kind { get; }

  /// <summary>The value of a boolean.</summary>
  public bool Bool { get; private init; }

  /// <summary>The bytes of a string.</summary>
  public byte[] Bytes { get; private init; } = [];

  /// <summary>The numerator of a number - the value itself for an integer.</summary>
  public BigInteger Numerator { get; private init; }

  /// <summary>The denominator of a number, positive; 1 for an integer.</summary>
  public BigInteger Denominator { get; private init; } = BigInteger.One;

  /// <summary>Whether a floating-point value is a binary floating-point number of <see cref="WidePrecision"/> bits rather than a fraction.</summary>
  public bool IsWide { get; private init; }

  /// <summary>The boolean <paramref name="value"/>.</summary>
  public static GoConstant MakeBool(bool value) => new(GoConstantKind.Bool) { Bool = value };

  /// <summary>The string of <paramref name="bytes"/>.</summary>
  public static GoConstant MakeString(byte[] bytes) => new(GoConstantKind.String) { Bytes = bytes };

  /// <summary>The string of the UTF-8 of <paramref name="text"/>.</summary>
  public static GoConstant MakeString(string text) => MakeString(Encoding.UTF8.GetBytes(text));

  /// <summary>The integer <paramref name="value"/>.</summary>
  public static GoConstant MakeInt(BigInteger value) => new(GoConstantKind.Int) { Numerator = value };

  /// <summary>A floating-point value of a fraction: the fraction while its parts are small, the wide number of it beyond (<c>makeRat</c>).</summary>
  public static GoConstant MakeRat(BigInteger numerator, BigInteger denominator) {
    if (denominator.Sign < 0)
      (numerator, denominator) = (-numerator, -denominator);

    var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
    if (!divisor.IsOne && !divisor.IsZero)
      (numerator, denominator) = (numerator / divisor, denominator / divisor);

    if (BitLength(numerator) < MaxExponent && BitLength(denominator) < MaxExponent)
      return new(GoConstantKind.Float) { Numerator = numerator, Denominator = denominator };

    return MakeWide(numerator, denominator);
  }

  /// <summary>The wide floating-point number nearest a fraction (<c>big.Float</c> of 512 bits, to nearest even).</summary>
  public static GoConstant MakeWide(BigInteger numerator, BigInteger denominator) {
    if (numerator.IsZero)
      return new(GoConstantKind.Float) { Numerator = BigInteger.Zero, IsWide = true };

    var (mantissa, scale) = Round(BigInteger.Abs(numerator), denominator, WidePrecision, int.MaxValue, RoundingMode.NearestEven);
    if (Math.Abs((long)BitLength(mantissa) - scale) > ExponentLimit)
      throw new GoConstantRangeException("a floating-point constant beyond the frontend's range of exponents");

    var signed = numerator.Sign < 0 ? -mantissa : mantissa;
    return scale >= 0
      ? Normalized(signed, BigInteger.One << scale)
      : Normalized(signed << -scale, BigInteger.One);

    static GoConstant Normalized(BigInteger numerator, BigInteger denominator) {
      var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
      return new(GoConstantKind.Float) { Numerator = numerator / divisor, Denominator = denominator / divisor, IsWide = true };
    }
  }

  /// <summary>The floating-point value of a double, exactly (<c>MakeFloat64</c>; -0 is 0); <see langword="null"/> for an infinity or a NaN.</summary>
  public static GoConstant? MakeFloat64(double value) {
    if (!double.IsFinite(value))
      return null;

    if (value == 0)
      return MakeRat(BigInteger.Zero, BigInteger.One);

    var bits = BitConverter.DoubleToInt64Bits(value);
    var exponent = (int)((bits >> 52) & 0x7FF);
    var mantissa = bits & 0xFFFFFFFFFFFFFL;
    if (exponent == 0)
      exponent = 1;
    else
      mantissa |= 1L << 52;

    var shift = exponent - 1075;
    var numerator = new BigInteger(mantissa) * (value < 0 ? -1 : 1);
    return shift >= 0 ? MakeRat(numerator << shift, BigInteger.One) : MakeRat(numerator, BigInteger.One << -shift);
  }

  /// <summary>The bit length of the magnitude of an integer.</summary>
  public static int BitLength(BigInteger value) => value.IsZero ? 0 : (int)BigInteger.Abs(value).GetBitLength();

  /// <summary>Whether the value is a number.</summary>
  public bool IsNumeric => this.Kind is GoConstantKind.Int or GoConstantKind.Float;

  /// <summary>The sign of a number: -1, 0 or 1.</summary>
  public int Sign => this.Numerator.Sign;

  /// <summary>Whether a number is an integer value, whatever its kind.</summary>
  public bool IsIntegral => this.IsNumeric && this.Denominator.IsOne;

  // ---------------------------------------------------------------- literals

  /// <summary>The value of an integer literal of any base, with separators (<c>strconv.ParseInt(lit, 0, …)</c>, then <c>big.Int.SetString</c>).</summary>
  public static GoConstant FromIntLiteral(string literal) {
    var text = literal.Replace("_", "", StringComparison.Ordinal);
    var (digits, @base) = Base(text);
    var value = BigInteger.Zero;
    foreach (var digit in digits)
      value = value * @base + HexDigit(digit);

    return MakeInt(value);
  }

  private static (string Digits, int Base) Base(string text) {
    if (text.Length >= 2 && text[0] == '0') {
      switch (char.ToLowerInvariant(text[1])) {
        case 'x':
          return (text[2..], 16);
        case 'o':
          return (text[2..], 8);
        case 'b':
          return (text[2..], 2);
        default:
          return (text[1..], 8);
      }
    }

    return (text, 10);
  }

  private static int HexDigit(char digit) => digit <= '9' ? digit - '0' : char.ToLowerInvariant(digit) - 'a' + 10;

  /// <summary>
  /// The value of a floating-point literal, decimal or hexadecimal (<c>makeFloatFromLiteral</c>): the
  /// exact fraction where the value's binary exponent is below 4096 in magnitude, the wide number otherwise.
  /// </summary>
  public static GoConstant FromFloatLiteral(string literal) {
    var text = literal.Replace("_", "", StringComparison.Ordinal);
    BigInteger mantissa;
    long exponent;
    int radix;
    if (text.Length >= 2 && text[0] == '0' && char.ToLowerInvariant(text[1]) == 'x') {
      radix = 2;
      var p = text.IndexOfAny(['p', 'P']);
      var (digits, fraction) = Split(text[2..p]);
      mantissa = Digits(digits + fraction, 16);
      exponent = long.Parse(text[(p + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) - 4L * fraction.Length;
    } else {
      radix = 10;
      var e = text.IndexOfAny(['e', 'E']);
      var body = e < 0 ? text : text[..e];
      var (digits, fraction) = Split(body);
      mantissa = Digits(digits + fraction, 10);
      var stated = e < 0 ? 0L : ParseExponent(text[(e + 1)..]);
      exponent = stated - fraction.Length;
    }

    if (mantissa.IsZero)
      return MakeRat(BigInteger.Zero, BigInteger.One);

    // The binary exponent of the value decides between a fraction and a wide number.
    var magnitude = BitLength(mantissa) + (radix == 2 ? exponent : (long)Math.Floor(exponent * 3.321928094887362));
    if (Math.Abs(magnitude) > ExponentLimit)
      throw new GoConstantRangeException($"the floating-point constant {literal} is beyond the frontend's range of exponents");

    var scale = radix == 2 ? BigInteger.Pow(2, (int)Math.Abs(exponent)) : BigInteger.Pow(10, (int)Math.Abs(exponent));
    var (numerator, denominator) = exponent >= 0 ? (mantissa * scale, BigInteger.One) : (mantissa, scale);
    var wide = MakeWide(numerator, denominator);
    // big.Float.MantExp: the exponent of 0.mantissa, one above the binary exponent of the value.
    var binary = BitLength(wide.Numerator) - BitLength(wide.Denominator) + 1;
    if (-MaxExponent < binary && binary < MaxExponent) {
      var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
      return new(GoConstantKind.Float) { Numerator = numerator / divisor, Denominator = denominator / divisor };
    }

    return wide;

    static (string Digits, string Fraction) Split(string body) {
      var dot = body.IndexOf('.', StringComparison.Ordinal);
      return dot < 0 ? (body, "") : (body[..dot], body[(dot + 1)..]);
    }

    static BigInteger Digits(string digits, int @base) {
      var value = BigInteger.Zero;
      foreach (var digit in digits)
        value = value * @base + HexDigit(digit);

      return value;
    }

    static long ParseExponent(string text)
      => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : text.StartsWith('-') ? -ExponentLimit * 4L : ExponentLimit * 4L;
  }

  // ---------------------------------------------------------------- printing

  /// <summary>The value as <c>constant.Value.ExactString</c> writes it: <c>true</c>, a quoted string, a decimal integer, a fraction <c>n/d</c>, or <c>0x.…p±e</c> for a wide number.</summary>
  public string ExactString => this.Kind switch {
    GoConstantKind.Bool => this.Bool ? "true" : "false",
    GoConstantKind.String => Quote(this.Bytes),
    GoConstantKind.Int => this.Numerator.ToString(CultureInfo.InvariantCulture),
    _ when this.IsWide => this.HexText(),
    _ when this.Denominator.IsOne => this.Numerator.ToString(CultureInfo.InvariantCulture),
    _ => $"{this.Numerator.ToString(CultureInfo.InvariantCulture)}/{this.Denominator.ToString(CultureInfo.InvariantCulture)}"
  };

  /// <summary>A short form for messages: the exact form of an integer or a short string, a decimal approximation of a fraction.</summary>
  public override string ToString() => this.Kind switch {
    GoConstantKind.Float when !this.Denominator.IsOne || this.IsWide => this.ApproximateText(),
    GoConstantKind.String when this.Bytes.Length > 64 => Quote(this.Bytes[..64]) + "...",
    _ => this.ExactString
  };

  private string ApproximateText() {
    var value = this.ToFloat64();
    return double.IsFinite(value) ? value.ToString("G6", CultureInfo.InvariantCulture).Replace("E+", "e+", StringComparison.Ordinal).Replace("E-", "e-", StringComparison.Ordinal) : this.ExactString;
  }

  /// <summary><c>big.Float.Text('p', 0)</c>: the hexadecimal mantissa after <c>0x.</c> without trailing zeros and the binary exponent of 0.mantissa.</summary>
  private string HexText() {
    if (this.Numerator.IsZero)
      return "0";

    var magnitude = BigInteger.Abs(this.Numerator);
    var scale = BitLength(this.Denominator) - 1;
    var bits = BitLength(magnitude);
    var exponent = bits - scale;
    var pad = (4 - bits % 4) % 4;
    var hex = (magnitude << pad).ToString("x", CultureInfo.InvariantCulture).TrimStart('0').TrimEnd('0');
    var sign = this.Numerator.Sign < 0 ? "-" : "";
    return $"{sign}0x.{hex}p{(exponent >= 0 ? "+" : "")}{exponent.ToString(CultureInfo.InvariantCulture)}";
  }

  /// <summary>A string as <c>strconv.Quote</c> writes it.</summary>
  public static string Quote(byte[] bytes) {
    var builder = new StringBuilder("\"");
    var index = 0;
    while (index < bytes.Length) {
      if (System.Text.Rune.DecodeFromUtf8(bytes.AsSpan(index), out var rune, out var width) != System.Buffers.OperationStatus.Done) {
        builder.Append(CultureInfo.InvariantCulture, $"\\x{bytes[index]:x2}");
        ++index;
        continue;
      }

      index += width;
      var value = rune.Value;
      switch (value) {
        case '"' or '\\':
          builder.Append('\\').Append((char)value);
          continue;
        case '\a':
          builder.Append("\\a");
          continue;
        case '\b':
          builder.Append("\\b");
          continue;
        case '\f':
          builder.Append("\\f");
          continue;
        case '\n':
          builder.Append("\\n");
          continue;
        case '\r':
          builder.Append("\\r");
          continue;
        case '\t':
          builder.Append("\\t");
          continue;
        case '\v':
          builder.Append("\\v");
          continue;
      }

      if (IsPrint(rune))
        builder.Append(rune.ToString());
      else if (value < ' ' || value == 0x7F)
        builder.Append(CultureInfo.InvariantCulture, $"\\x{value:x2}");
      else if (value < 0x10000)
        builder.Append(CultureInfo.InvariantCulture, $"\\u{value:x4}");
      else
        builder.Append(CultureInfo.InvariantCulture, $"\\U{value:x8}");
    }

    return builder.Append('"').ToString();
  }

  /// <summary><c>strconv.IsPrint</c>: letters, marks, numbers, punctuation, symbols and the ASCII space.</summary>
  private static bool IsPrint(System.Text.Rune rune) {
    if (rune.Value == ' ')
      return true;

    return System.Text.Rune.GetUnicodeCategory(rune) switch {
      UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
        or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
        or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or UnicodeCategory.ConnectorPunctuation
        or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
        or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol
        or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol => true,
      _ => false
    };
  }

  // ---------------------------------------------------------------- conversions

  /// <summary>The integer a number denotes (<c>constant.ToInt</c>): itself, an integral fraction, a wide number within 4 bits of an integer; <see langword="null"/> otherwise.</summary>
  public GoConstant? ToInt() {
    switch (this.Kind) {
      case GoConstantKind.Int:
        return this;
      case GoConstantKind.Float when !this.IsWide:
        return this.Denominator.IsOne ? MakeInt(this.Numerator) : null;
      case GoConstantKind.Float:
        var binary = BitLength(this.Numerator) - BitLength(this.Denominator) + 1;
        if (binary <= -MaxExponent || binary >= MaxExponent)
          return null;

        if (this.Denominator.IsOne)
          return MakeInt(this.Numerator);

        foreach (var mode in new[] { RoundingMode.TowardZero, RoundingMode.AwayFromZero }) {
          var (mantissa, scale) = Round(BigInteger.Abs(this.Numerator), this.Denominator, WidePrecision - 4, int.MaxValue, mode);
          if (scale <= 0)
            return MakeInt((mantissa << -scale) * this.Numerator.Sign);

          if ((mantissa & ((BigInteger.One << scale) - 1)).IsZero)
            return MakeInt((mantissa >> scale) * this.Numerator.Sign);
        }

        return null;
      default:
        return null;
    }
  }

  /// <summary>The floating-point value of a number (<c>constant.ToFloat</c>): a fraction of an integer, a float as it is; <see langword="null"/> for no number.</summary>
  public GoConstant? ToFloat() => this.Kind switch {
    GoConstantKind.Int => new GoConstant(GoConstantKind.Float) { Numerator = this.Numerator },
    GoConstantKind.Float => this,
    _ => null
  };

  /// <summary>The nearest double, an infinity beyond its range (<c>constant.Float64Val</c>).</summary>
  public double ToFloat64() => ToBinary(this.Numerator, this.Denominator, 53, 1074, 1023);

  /// <summary>The nearest single, an infinity beyond its range (<c>constant.Float32Val</c>).</summary>
  public float ToFloat32() => (float)ToBinary(this.Numerator, this.Denominator, 24, 149, 127);

  private static double ToBinary(BigInteger numerator, BigInteger denominator, int precision, int subnormalScale, int maxExponent) {
    if (numerator.IsZero)
      return 0;

    var magnitude = BigInteger.Abs(numerator);
    var exponent = Floor2(magnitude, denominator);
    if (exponent > maxExponent)
      return numerator.Sign < 0 ? double.NegativeInfinity : double.PositiveInfinity;

    var (mantissa, scale) = Round(magnitude, denominator, precision, subnormalScale, RoundingMode.NearestEven);
    var value = Math.ScaleB((double)mantissa, -scale);
    return numerator.Sign < 0 ? -value : value;
  }

  /// <summary>The binary exponent of a positive fraction: the e with 2^e ≤ n/d &lt; 2^(e+1).</summary>
  private static int Floor2(BigInteger numerator, BigInteger denominator) {
    var exponent = BitLength(numerator) - BitLength(denominator);
    var (a, b) = exponent >= 0 ? (numerator, denominator << exponent) : (numerator << -exponent, denominator);
    return a < b ? exponent - 1 : exponent;
  }

  /// <summary>How a value between two representable ones is rounded.</summary>
  private enum RoundingMode {
    NearestEven,
    TowardZero,
    AwayFromZero
  }

  /// <summary>A positive fraction rounded to a mantissa of at most <paramref name="precision"/> bits: m · 2^-s, the scale s never above <paramref name="maxScale"/> (the subnormal numbers).</summary>
  private static (BigInteger Mantissa, int Scale) Round(BigInteger numerator, BigInteger denominator, int precision, int maxScale, RoundingMode mode) {
    var exponent = Floor2(numerator, denominator);
    var scale = (int)Math.Min((long)precision - 1 - exponent, maxScale);
    var (a, b) = scale >= 0 ? (numerator << scale, denominator) : (numerator, denominator << -scale);
    var quotient = BigInteger.DivRem(a, b, out var remainder);
    if (!remainder.IsZero) {
      var up = mode switch {
        RoundingMode.TowardZero => false,
        RoundingMode.AwayFromZero => true,
        _ => (remainder * 2).CompareTo(b) is var order && (order > 0 || order == 0 && !quotient.IsEven)
      };
      if (up)
        ++quotient;
    }

    if (BitLength(quotient) > precision) {
      quotient >>= 1;
      --scale;
    }

    return (quotient, scale);
  }

  // ---------------------------------------------------------------- operations

  private static GoConstant Float(GoConstant value) => value.Kind == GoConstantKind.Int ? value.ToFloat()! : value;

  private static GoConstant Wide(GoConstant value) => value.IsWide ? value : MakeWide(value.Numerator, value.Denominator);

  /// <summary>Brings two numbers to one representation (<c>match</c>): integer, then fraction, then wide number.</summary>
  private static (GoConstant, GoConstant) Match(GoConstant x, GoConstant y) {
    if (x.Kind == GoConstantKind.Int && y.Kind == GoConstantKind.Int)
      return (x, y);

    if (x.IsWide || y.IsWide)
      return (Wide(Float(x)), Wide(Float(y)));

    return (Float(x), Float(y));
  }

  /// <summary>
  /// A binary operation (<c>constant.BinaryOp</c>) on two values of one kind: <c>+ - * / % &amp; | ^ &amp;^</c> on
  /// numbers, <c>/</c> exact on floats and truncating where <paramref name="integerDivision"/>, <c>+</c> on
  /// strings, <c>&amp;&amp; ||</c> on booleans.
  /// </summary>
  public static GoConstant Binary(GoConstant x, string op, GoConstant y, bool integerDivision) {
    switch (x.Kind) {
      case GoConstantKind.Bool:
        return MakeBool(op == "&&" ? x.Bool && y.Bool : x.Bool || y.Bool);
      case GoConstantKind.String:
        return MakeString([.. x.Bytes, .. y.Bytes]);
    }

    var (a, b) = Match(x, y);
    if (a.Kind == GoConstantKind.Int) {
      var (p, q) = (a.Numerator, b.Numerator);
      switch (op) {
        case "+":
          return MakeInt(p + q);
        case "-":
          return MakeInt(p - q);
        case "*":
          return MakeInt(p * q);
        case "/" when integerDivision:
          return MakeInt(BigInteger.Divide(p, q));
        case "/":
          return MakeRat(p, q);
        case "%":
          return MakeInt(BigInteger.Remainder(p, q));
        case "&":
          return MakeInt(p & q);
        case "|":
          return MakeInt(p | q);
        case "^":
          return MakeInt(p ^ q);
        default:
          return MakeInt(p & ~q);
      }
    }

    var (n, d) = op switch {
      "+" => (a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator),
      "-" => (a.Numerator * b.Denominator - b.Numerator * a.Denominator, a.Denominator * b.Denominator),
      "*" => (a.Numerator * b.Numerator, a.Denominator * b.Denominator),
      _ => (a.Numerator * b.Denominator, a.Denominator * b.Numerator)
    };
    return a.IsWide ? MakeWide(n, d) : MakeRat(n, d);
  }

  /// <summary>A unary operation (<c>constant.UnaryOp</c>): <c>+ -</c> of a number, <c>^</c> of an integer - in <paramref name="precision"/> bits where it is not 0 - and <c>!</c> of a boolean.</summary>
  public static GoConstant Unary(string op, GoConstant y, int precision) {
    switch (op) {
      case "+":
        return y;
      case "!":
        return MakeBool(!y.Bool);
      case "^": {
        var complement = -y.Numerator - 1;
        if (precision > 0)
          complement &= (BigInteger.One << precision) - 1;

        return MakeInt(complement);
      }
      default:
        return y.Kind == GoConstantKind.Int
          ? MakeInt(-y.Numerator)
          : new GoConstant(GoConstantKind.Float) { Numerator = -y.Numerator, Denominator = y.Denominator, IsWide = y.IsWide };
    }
  }

  /// <summary>A shift of an integer (<c>constant.Shift</c>); a right shift rounds toward negative infinity.</summary>
  public static GoConstant Shift(GoConstant x, string op, int count) => MakeInt(op == "<<" ? x.Numerator << count : x.Numerator >> count);

  /// <summary>A comparison (<c>constant.Compare</c>) of two values of one kind.</summary>
  public static bool Compare(GoConstant x, string op, GoConstant y) {
    int order;
    switch (x.Kind) {
      case GoConstantKind.Bool:
        order = x.Bool == y.Bool ? 0 : 1;
        break;
      case GoConstantKind.String:
        order = CompareBytes(x.Bytes, y.Bytes);
        break;
      default:
        var (a, b) = Match(x, y);
        order = (a.Numerator * b.Denominator).CompareTo(b.Numerator * a.Denominator);
        break;
    }

    return op switch {
      "==" => order == 0,
      "!=" => order != 0,
      "<" => order < 0,
      "<=" => order <= 0,
      ">" => order > 0,
      _ => order >= 0
    };
  }

  private static int CompareBytes(byte[] x, byte[] y) {
    for (var index = 0; index < Math.Min(x.Length, y.Length); ++index)
      if (x[index] != y[index])
        return x[index].CompareTo(y[index]);

    return x.Length.CompareTo(y.Length);
  }

  /// <inheritdoc/>
  public bool Equals(GoConstant? other) => other is not null && this.Kind == other.Kind && this.Bool == other.Bool && this.Bytes.SequenceEqual(other.Bytes)
    && this.Numerator == other.Numerator && this.Denominator == other.Denominator;

  /// <inheritdoc/>
  public override bool Equals(object? obj) => this.Equals(obj as GoConstant);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Kind, this.Bool, this.Bytes.Length, this.Numerator, this.Denominator);

  /// <summary>The UTF-8 of a code point, the replacement character for one that is no valid code point (a conversion <c>string(r)</c>).</summary>
  public static byte[] Utf8Of(BigInteger point) {
    var valid = point >= 0 && point <= 0x10FFFF && !(point >= 0xD800 && point < 0xE000);
    return Encoding.UTF8.GetBytes(char.ConvertFromUtf32(valid ? (int)point : 0xFFFD));
  }

  /// <summary>Decodes the literal of a string, interpreted or raw, into its bytes (<c>strconv.Unquote</c> of a literal the scanner accepted).</summary>
  public static byte[] Unquote(string literal) {
    if (literal[0] == '`')
      return Encoding.UTF8.GetBytes(literal[1..^1]);

    var bytes = new List<byte>();
    var body = literal[1..^1];
    for (var index = 0; index < body.Length;) {
      if (body[index] != '\\') {
        var width = char.IsHighSurrogate(body[index]) && index + 1 < body.Length ? 2 : 1;
        bytes.AddRange(Encoding.UTF8.GetBytes(body.Substring(index, width)));
        index += width;
        continue;
      }

      var (value, isByte, next) = Escape(body, index + 1);
      if (isByte)
        bytes.Add((byte)value);
      else
        bytes.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(value)));

      index = next;
    }

    return [.. bytes];
  }

  /// <summary>The code point of a rune literal the scanner accepted.</summary>
  public static int RuneOf(string literal) {
    var body = literal[1..^1];
    if (body[0] != '\\')
      return char.ConvertToUtf32(body, 0);

    return Escape(body, 1).Value;
  }

  /// <summary>An escape after its backslash: the value, whether it is a byte (<c>\x</c>, octal) rather than a code point, and the index after it.</summary>
  private static (int Value, bool IsByte, int Next) Escape(string body, int index) {
    var escape = body[index];
    switch (escape) {
      case 'a': return (7, false, index + 1);
      case 'b': return (8, false, index + 1);
      case 'f': return (12, false, index + 1);
      case 'n': return (10, false, index + 1);
      case 'r': return (13, false, index + 1);
      case 't': return (9, false, index + 1);
      case 'v': return (11, false, index + 1);
      case '\\' or '\'' or '"': return (escape, false, index + 1);
      case 'x': return (int.Parse(body.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture), true, index + 3);
      case 'u': return (int.Parse(body.AsSpan(index + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture), false, index + 5);
      case 'U': return (int.Parse(body.AsSpan(index + 1, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture), false, index + 9);
      default: return (Convert.ToInt32(body.Substring(index, 3), 8), true, index + 3);
    }
  }

}
