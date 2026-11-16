namespace Polypiler.Core.Backends.Python;

internal static partial class PythonHelpers {

  /// <summary>ECMA-262's <c>Number::toString</c> of a double.</summary>
  public const string FormatEcma = "_ply_format_ecma";

  /// <summary>.NET's <c>double.ToString()</c> or <c>float.ToString()</c> with the invariant culture.</summary>
  public const string FormatDotNet = "_ply_format_net";

  /// <summary>The digits of an integer in a radix, padded, in two's complement or with a sign.</summary>
  public const string FormatRadix = "_ply_radix";

  /// <summary>ECMA-262's <c>toFixed</c>.</summary>
  public const string FixedEcma = "_ply_fixed_ecma";

  /// <summary>.NET's <c>ToString("F&lt;n&gt;")</c>.</summary>
  public const string FixedDotNet = "_ply_fixed_net";

  /// <summary>ECMA-262's <c>Number(text)</c>.</summary>
  public const string ParseNumberEcma = "_ply_parse_number_ecma";

  /// <summary>ECMA-262's <c>parseFloat</c>.</summary>
  public const string ParseFloatEcma = "_ply_parse_float_ecma";

  /// <summary>ECMA-262's <c>parseInt</c>.</summary>
  public const string ParseIntEcma = "_ply_parse_int_ecma";

  /// <summary>.NET's <c>Parse</c> of an integer type.</summary>
  public const string ParseIntDotNet = "_ply_parse_int_net";

  /// <summary>.NET's <c>double.Parse</c>.</summary>
  public const string ParseFloatDotNet = "_ply_parse_float_net";

  /// <summary>
  /// The conversions between numbers and text of .NET and of ECMA-262, whose layouts and grammars are
  /// not Python's: the shortest digits come from <c>repr</c>, which ECMA-262 and .NET Core 3.0 agree
  /// with, the exact ones from <c>float.as_integer_ratio</c>; the rules of each are those of
  /// <c>NumberTextOperations</c> of the IR.
  /// </summary>
  private const string NumberTextSource = """
    def _ply_shortest(value: float, single: bool) -> tuple:
        text = repr(abs(value))
        if single:
            for precision in range(1, 10):
                candidate = format(abs(value), "." + str(precision - 1) + "e")
                if _ply_f32(float(candidate)) == abs(value):
                    text = candidate
                    break
        mantissa = text
        exponent = 0
        if "e" in text:
            mantissa = text[:text.index("e")]
            exponent = int(text[text.index("e") + 1:])
        whole = mantissa
        fraction = ""
        if "." in mantissa:
            whole = mantissa[:mantissa.index(".")]
            fraction = mantissa[mantissa.index(".") + 1:]
        digits = whole + fraction
        leading = len(digits) - len(digits.lstrip("0"))
        digits = digits.strip("0")
        return (digits, exponent + len(whole) - 1 - leading)


    def _ply_format_ecma(value: float) -> str:
        if value != value:
            return "NaN"
        if value == 0.0:
            return "0"
        if math.isinf(value):
            return "-Infinity" if value < 0.0 else "Infinity"
        shortest = _ply_shortest(value, False)
        digits = shortest[0]
        exponent = shortest[1]
        sign = "-" if value < 0.0 else ""
        k = len(digits)
        n = exponent + 1
        if k <= n <= 21:
            return sign + digits + "0" * (n - k)
        if 0 < n <= 21:
            return sign + digits[:n] + "." + digits[n:]
        if -6 < n <= 0:
            return sign + "0." + "0" * -n + digits
        power = ("e-" if exponent < 0 else "e+") + str(abs(exponent))
        return sign + digits[:1] + ("" if k == 1 else "." + digits[1:]) + power


    def _ply_format_net(value: float, single: bool) -> str:
        if value != value:
            return "NaN"
        if math.isinf(value):
            return "-Infinity" if value < 0.0 else "Infinity"
        sign = "-" if math.copysign(1.0, value) < 0.0 else ""
        if value == 0.0:
            return sign + "0"
        shortest = _ply_shortest(value, single)
        digits = shortest[0]
        scale = shortest[1] + 1
        if scale > max(len(digits), 7 if single else 15) or scale < -3:
            power = str(abs(scale - 1)).rjust(2, "0")
            mantissa = digits[:1] + ("" if len(digits) == 1 else "." + digits[1:])
            return sign + mantissa + ("E-" if scale - 1 < 0 else "E+") + power
        if scale <= 0:
            return sign + "0." + "0" * -scale + digits
        if scale >= len(digits):
            return sign + digits + "0" * (scale - len(digits))
        return sign + digits[:scale] + "." + digits[scale:]


    def _ply_radix(value: int, bits: int, radix: int, width: int, upper: bool, twos: bool) -> str:
        negative = not twos and value < 0
        magnitude = -value if negative else value & (1 << bits) - 1 if twos else value
        text = ""
        while True:
            text = "0123456789abcdefghijklmnopqrstuvwxyz"[magnitude % radix] + text
            magnitude = magnitude // radix
            if magnitude == 0:
                break
        text = text.rjust(width, "0")
        if upper:
            text = text.upper()
        return "-" + text if negative else text


    def _ply_fixed_digits(value: float, digits: int, ties_up: bool) -> str:
        ratio = value.as_integer_ratio()
        scaled = ratio[0] * 10 ** digits
        rounded = scaled // ratio[1]
        rest = (scaled - rounded * ratio[1]) * 2
        if rest > ratio[1] or rest == ratio[1] and (ties_up or rounded % 2 == 1):
            rounded = rounded + 1
        text = str(rounded).rjust(digits + 1, "0")
        if digits == 0:
            return text
        return text[:len(text) - digits] + "." + text[len(text) - digits:]


    def _ply_fixed_ecma(value: float, digits: int) -> str:
        if value != value or abs(value) >= 1e21:
            return _ply_format_ecma(value)
        return ("-" if value < 0.0 else "") + _ply_fixed_digits(abs(value), digits, True)


    def _ply_fixed_net(value: float, digits: int) -> str:
        if value != value:
            return "NaN"
        if math.isinf(value):
            return "-Infinity" if value < 0.0 else "Infinity"
        sign = "-" if math.copysign(1.0, value) < 0.0 else ""
        return sign + _ply_fixed_digits(abs(value), digits, False)


    def _ply_decimal_literal(text: str, start: int) -> int:
        at = start
        digits = 0
        while at < len(text) and "0" <= text[at] <= "9":
            at = at + 1
            digits = digits + 1
        if at < len(text) and text[at] == ".":
            point = at
            at = at + 1
            while at < len(text) and "0" <= text[at] <= "9":
                at = at + 1
                digits = digits + 1
            if digits == 0:
                return point - start
        if digits == 0:
            return 0
        if at < len(text) and text[at] in "eE":
            mark = at + 1
            if mark < len(text) and text[mark] in "+-":
                mark = mark + 1
            first = mark
            while mark < len(text) and "0" <= text[mark] <= "9":
                mark = mark + 1
            if mark > first:
                at = mark
        return at - start


    def _ply_digit(unit: str, radix: int) -> int:
        value = 99
        if "0" <= unit <= "9":
            value = ord(unit) - 48
        elif "a" <= unit <= "z":
            value = ord(unit) - 87
        elif "A" <= unit <= "Z":
            value = ord(unit) - 55
        return value if value < radix else -1


    def _ply_nearest(value: int) -> float:
        try:
            return float(value)
        except OverflowError:
            return math.inf


    def _ply_parse_number_ecma(text: str) -> float:
        trimmed = _ply_trim_ecma(text, True, True)
        if trimmed == "":
            return 0.0
        if len(trimmed) > 2 and trimmed[0] == "0" and trimmed[1] in "xXoObB":
            radix = 16 if trimmed[1] in "xX" else 8 if trimmed[1] in "oO" else 2
            value = 0
            for unit in trimmed[2:]:
                digit = _ply_digit(unit, radix)
                if digit < 0:
                    return math.nan
                value = value * radix + digit
            return _ply_nearest(value)
        start = 1 if trimmed[0] in "+-" else 0
        if trimmed[start:] == "Infinity":
            return -math.inf if trimmed[0] == "-" else math.inf
        if len(trimmed) > start and _ply_decimal_literal(trimmed, start) == len(trimmed) - start:
            return float(trimmed)
        return math.nan


    def _ply_parse_float_ecma(text: str) -> float:
        trimmed = _ply_trim_ecma(text, True, False)
        start = 1 if trimmed[:1] in ("+", "-") and trimmed != "" else 0
        if trimmed[start:start + 8] == "Infinity":
            return -math.inf if trimmed[0] == "-" else math.inf
        length = _ply_decimal_literal(trimmed, start)
        if length == 0:
            return math.nan
        return float(trimmed[:start + length])


    def _ply_parse_int_ecma(text: str, radix: int) -> float:
        trimmed = _ply_trim_ecma(text, True, False)
        negative = trimmed[:1] == "-"
        at = 1 if trimmed[:1] in ("+", "-") and trimmed != "" else 0
        strip = True
        if radix != 0:
            if radix < 2 or radix > 36:
                return math.nan
            strip = radix == 16
        else:
            radix = 10
        if strip and len(trimmed) - at >= 2 and trimmed[at] == "0" and trimmed[at + 1] in "xX":
            at = at + 2
            radix = 16
        first = at
        while at < len(trimmed) and _ply_digit(trimmed[at], radix) >= 0:
            at = at + 1
        if at == first:
            return math.nan
        digits = trimmed[first:at]
        if radix in (2, 4, 8, 10, 16, 32):
            value = 0
            for unit in digits:
                value = value * radix + _ply_digit(unit, radix)
            number = _ply_nearest(value)
        else:
            number = 0.0
            digits = digits.lstrip("0")
            index = 0
            while index < len(digits):
                part = 0
                multiplier = 1
                while index < len(digits) and multiplier * radix <= 119304647:
                    part = part * radix + _ply_digit(digits[index], radix)
                    multiplier = multiplier * radix
                    index = index + 1
                number = number * multiplier + part
        return -number if negative else number


    def _ply_parse_int_net(text: str, low: int, high: int) -> int:
        trimmed = text.strip("\t\n\x0b\x0c\r ")
        sign = 1
        if trimmed[:1] == "-":
            sign = -1
            trimmed = trimmed[1:]
        elif trimmed[:1] == "+":
            trimmed = trimmed[1:]
        if trimmed == "":
            raise FormatError("invalid format")
        for unit in trimmed:
            if unit < "0" or unit > "9":
                raise FormatError("invalid format")
        value = sign * int(trimmed)
        if value < low or value > high:
            raise OverflowError("integer overflow")
        return value


    def _ply_parse_float_net(text: str, single: bool) -> float:
        trimmed = text.strip("\t\n\x0b\x0c\r ")
        sign = ""
        if trimmed[:1] in ("+", "-") and trimmed != "":
            sign = trimmed[0]
            trimmed = trimmed[1:]
        lowered = trimmed.lower()
        if lowered in ("infinity", "∞"):
            return -math.inf if sign == "-" else math.inf
        if lowered == "nan":
            return math.nan
        at = 0
        whole = ""
        while at < len(trimmed) and ("0" <= trimmed[at] <= "9" or trimmed[at] == "," and whole != ""):
            if trimmed[at] != ",":
                whole = whole + trimmed[at]
            at = at + 1
        fraction = ""
        if at < len(trimmed) and trimmed[at] == ".":
            at = at + 1
            while at < len(trimmed) and "0" <= trimmed[at] <= "9":
                fraction = fraction + trimmed[at]
                at = at + 1
        if whole == "" and fraction == "":
            raise FormatError("invalid format")
        power = ""
        if at < len(trimmed) and trimmed[at] in "eE":
            mark = at + 1
            if mark < len(trimmed) and trimmed[mark] in "+-":
                mark = mark + 1
            first = mark
            while mark < len(trimmed) and "0" <= trimmed[mark] <= "9":
                mark = mark + 1
            if mark == first:
                raise FormatError("invalid format")
            power = trimmed[at:mark]
            at = mark
        if at != len(trimmed):
            raise FormatError("invalid format")
        value = float(sign + (whole if whole != "" else "0") + "." + fraction + "0" + power)
        return _ply_f32(value) if single else value
    """;

}
