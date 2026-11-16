using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Python.Syntax;

namespace Polypiler.Core.Backends.Python;

/// <summary>
/// The helpers that stay with the Python backend: each is an operation of the IR written with what
/// Python has, where Python has no expression for it. Python cannot raise inside an expression, so
/// every operation that traps is a function - the checked range of an integer, the truncating
/// division and its remainder with their two traps, an index that must not count from the end, a
/// length that must not be negative, the conversions of a float to an integer that saturate or
/// trap - and Python's float division and remainder raise where IEEE 754 gives an infinity or a NaN.
/// Python has no binary32 type, so rounding to it is a function of the <c>struct</c> module, and an
/// integer is rounded to binary32 directly, never through a double, which would round twice. Each is
/// read into the syntax tree once and declared in a module only when the module names it.
/// </summary>
internal static partial class PythonHelpers {

  /// <summary>The folder of the helpers in the repository, which their origin names.</summary>
  public const string Folder = "Polypiler.Core.Backends.Python/Helpers";

  /// <summary>Raises the overflow trap unless an integer lies in a range.</summary>
  public const string Checked = "_ply_checked";

  /// <summary>The quotient of two integers truncated toward zero; the overflow trap for the smallest value divided by -1.</summary>
  public const string Divide = "_ply_sdiv";

  /// <summary>The remainder with the sign of the dividend; the overflow trap where <see cref="Divide"/> has it.</summary>
  public const string Remainder = "_ply_srem";

  /// <summary>Raises the bounds trap for a negative index, which Python would count from the end.</summary>
  public const string Index = "_ply_index";

  /// <summary>Raises the overflow trap for a negative length of a new array, which Python would make empty.</summary>
  public const string Length = "_ply_length";

  /// <summary>IEEE 754 division of two doubles, which gives an infinity or a NaN for a zero divisor.</summary>
  public const string FloatDivide = "_ply_fdiv";

  /// <summary>The remainder of two doubles as C's <c>fmod</c>, which gives a NaN where Python raises.</summary>
  public const string FloatRemainder = "_ply_fmod";

  /// <summary>A double rounded to the nearest binary32, ties to even.</summary>
  public const string Single = "_ply_f32";

  /// <summary>An integer rounded to the nearest binary32 once, ties to even.</summary>
  public const string SingleOfInteger = "_ply_f32_of_int";

  /// <summary>A float truncated toward zero and saturated to a range of integers, a NaN becoming 0.</summary>
  public const string Saturated = "_ply_f2i_sat";

  /// <summary>A float truncated toward zero; the overflow trap for a NaN and a value outside a range.</summary>
  public const string Truncated = "_ply_f2i_trap";

  /// <summary>The square root of IEEE 754, a NaN for a negative operand where Python raises.</summary>
  public const string SquareRoot = "_ply_sqrt";

  /// <summary>The smaller of two doubles as IEEE 754-2019's minimum: a NaN if either is one, -0 below +0.</summary>
  public const string Minimum = "_ply_fmin";

  /// <summary>The larger of two doubles as IEEE 754-2019's maximum: a NaN if either is one, +0 above -0.</summary>
  public const string Maximum = "_ply_fmax";

  /// <summary>The sign of a double as -1, 0 or 1; the invalid-operation trap for a NaN.</summary>
  public const string Sign = "_ply_fsign";

  /// <summary>The largest integral double not above a double, infinities and NaNs as they are.</summary>
  public const string Floor = "_ply_floor";

  /// <summary>The smallest integral double not below a double, -0 for a value in (-1, 0).</summary>
  public const string Ceiling = "_ply_ceil";

  /// <summary>A double without its fraction, with its sign.</summary>
  public const string Truncate = "_ply_trunc";

  /// <summary>The nearest integral double, a tie to the even one.</summary>
  public const string RoundEven = "_ply_round_even";

  /// <summary>The nearest integral double, a tie away from zero.</summary>
  public const string RoundAway = "_ply_round_away";

  /// <summary>The quotient of two arbitrary-precision integers truncated toward zero; <c>ZeroDivisionError</c> only.</summary>
  public const string BigDivide = "_ply_big_div";

  /// <summary>The remainder of two arbitrary-precision integers with the sign of the dividend.</summary>
  public const string BigRemainder = "_ply_big_rem";

  /// <summary>An arbitrary-precision shift to the left by a count of any sign, a negative one shifting to the right.</summary>
  public const string BigShiftLeft = "_ply_big_shl";

  /// <summary>An arbitrary-precision shift to the right by a count of any sign, a negative one shifting to the left.</summary>
  public const string BigShiftRight = "_ply_big_shr";

  /// <summary>The nearest double to an integer, an infinity where Python's <c>float</c> raises <c>OverflowError</c>.</summary>
  public const string BigToFloat = "_ply_big_float";

  /// <summary>A float truncated toward zero to an integer of any magnitude; the overflow trap for a NaN and an infinity.</summary>
  public const string FloatToBig = "_ply_float_big";

  /// <summary>ECMA-262's <c>Math.round</c>: the nearest integral double, a tie toward positive infinity, the sign kept on a zero.</summary>
  public const string RoundHalfUp = "_ply_round_half_up";

  /// <summary>The source of every helper, in the order a module declares them.</summary>
  private const string Source = """
    def _ply_checked(value: int, low: int, high: int) -> int:
        if value < low or value > high:
            raise OverflowError("integer overflow")
        return value


    def _ply_sdiv(dividend: int, divisor: int, low: int) -> int:
        quotient = abs(dividend) // abs(divisor)
        if (dividend < 0) != (divisor < 0):
            quotient = -quotient
        if quotient == -low:
            raise OverflowError("integer overflow")
        return quotient


    def _ply_srem(dividend: int, divisor: int, low: int) -> int:
        if divisor == -1 and dividend == low:
            raise OverflowError("integer overflow")
        remainder = abs(dividend) % abs(divisor)
        return -remainder if dividend < 0 else remainder


    def _ply_index(index: int) -> int:
        if index < 0:
            raise IndexError("index out of range")
        return index


    def _ply_length(length: int) -> int:
        if length < 0:
            raise OverflowError("integer overflow")
        return length


    def _ply_fdiv(dividend: float, divisor: float) -> float:
        if divisor != 0.0:
            return dividend / divisor
        if dividend != dividend:
            return dividend
        if dividend == 0.0:
            return math.nan
        return math.copysign(math.inf, dividend) * math.copysign(1.0, divisor)


    def _ply_fmod(dividend: float, divisor: float) -> float:
        if dividend != dividend:
            return dividend
        if divisor != divisor:
            return divisor
        if divisor == 0.0 or math.isinf(dividend):
            return math.nan
        return math.fmod(dividend, divisor)


    def _ply_f32(value: float) -> float:
        if abs(value) >= 3.4028235677973366e+38:
            return math.copysign(math.inf, value)
        return struct.unpack("<f", struct.pack("<f", value))[0]


    def _ply_f32_of_int(value: int) -> float:
        magnitude = abs(value)
        shift = magnitude.bit_length() - 24
        if shift > 0:
            rest = magnitude & (1 << shift) - 1
            magnitude = magnitude >> shift
            half = 1 << shift - 1
            if rest > half or rest == half and magnitude & 1 == 1:
                magnitude = magnitude + 1
            magnitude = magnitude << shift
        return float(-magnitude if value < 0 else magnitude)


    def _ply_f2i_sat(value: float, low: int, high: int) -> int:
        if value != value:
            return 0
        if value <= low:
            return low
        if value >= high:
            return high
        return int(value)


    def _ply_f2i_trap(value: float, low: int, high: int) -> int:
        if value != value or value <= low - 1 or value >= high + 1:
            raise OverflowError("integer overflow")
        return int(value)


    def _ply_sqrt(value: float) -> float:
        if value < 0.0:
            return math.nan
        return math.sqrt(value)


    def _ply_fmin(left: float, right: float) -> float:
        if left != left:
            return left
        if right != right:
            return right
        if left == right:
            return left if math.copysign(1.0, left) < 0.0 else right
        return left if left < right else right


    def _ply_fmax(left: float, right: float) -> float:
        if left != left:
            return left
        if right != right:
            return right
        if left == right:
            return right if math.copysign(1.0, left) < 0.0 else left
        return left if left > right else right


    def _ply_fsign(value: float) -> int:
        if value != value:
            raise ArithmeticError("invalid operation")
        return (value > 0.0) - (value < 0.0)


    def _ply_floor(value: float) -> float:
        if not math.isfinite(value):
            return value
        return math.copysign(float(math.floor(value)), value)


    def _ply_ceil(value: float) -> float:
        if not math.isfinite(value):
            return value
        return math.copysign(float(math.ceil(value)), value)


    def _ply_trunc(value: float) -> float:
        if not math.isfinite(value):
            return value
        return math.copysign(float(math.trunc(value)), value)


    def _ply_round_even(value: float) -> float:
        if not math.isfinite(value):
            return value
        return math.copysign(float(round(value)), value)


    def _ply_round_away(value: float) -> float:
        if not math.isfinite(value) or abs(value) >= 4503599627370496.0:
            return value
        whole = math.trunc(value)
        if abs(value - whole) >= 0.5:
            whole = whole + (1 if value > 0.0 else -1)
        return math.copysign(float(whole), value)


    def _ply_big_div(dividend: int, divisor: int) -> int:
        quotient = abs(dividend) // abs(divisor)
        return -quotient if (dividend < 0) != (divisor < 0) else quotient


    def _ply_big_rem(dividend: int, divisor: int) -> int:
        remainder = abs(dividend) % abs(divisor)
        return -remainder if dividend < 0 else remainder


    def _ply_big_shl(value: int, count: int) -> int:
        return value << count if count >= 0 else value >> -count


    def _ply_big_shr(value: int, count: int) -> int:
        return value >> count if count >= 0 else value << -count


    def _ply_big_float(value: int) -> float:
        if abs(value) >= (1 << 1024) - (1 << 970):
            return math.inf if value > 0 else -math.inf
        return float(value)


    def _ply_float_big(value: float) -> int:
        if not math.isfinite(value):
            raise OverflowError("integer overflow")
        return int(value)


    def _ply_round_half_up(value: float) -> float:
        if not math.isfinite(value):
            return value
        floor = float(math.floor(value))
        rounded = floor + 1.0 if value - floor >= 0.5 else floor
        return math.copysign(0.0, value) if rounded == 0.0 else rounded
    """;

  /// <summary>Every helper's declaration - a function, or the class of a representation - in the order a module declares them.</summary>
  private static readonly Lazy<IReadOnlyList<PyStatement>> _declarations = new(() => [
    .. PythonSyntaxReader.ReadModule(string.Join("\n\n\n", Source, ObjectsSource, StringsSource, CollectionsSource, ViewsSource, PointersSource, NumberTextSource)).Body
      .Select(statement => statement switch {
        PyFunctionDef function => function with { IsHelper = true },
        // A blank line in front of every method of a class but the first (PEP 8).
        PyClassDef type => type with { Body = [.. type.Body.Select((member, index) => index > 0 && member is PyFunctionDef ? member with { Leading = PyTrivia.Blank } : member)] },
        _ => statement
      })
  ]);

  /// <summary>The name a helper's declaration declares.</summary>
  private static string NameOf(PyStatement statement) => statement switch {
    PyFunctionDef function => function.Name,
    PyClassDef type => type.Name,
    _ => throw new InvalidOperationException("A helper is a function or a class.")
  };

  /// <summary>The names of the helpers, in the order a module declares them.</summary>
  public static IReadOnlyList<string> Order => [.. _declarations.Value.Select(NameOf)];

  /// <summary>The declaration of a helper.</summary>
  /// <param name="name">The name of the helper.</param>
  /// <returns>The function, marked as a helper, or the class.</returns>
  /// <exception cref="InvalidOperationException">There is no helper of that name.</exception>
  public static PyStatement Declaration(string name) => _declarations.Value.First(statement => NameOf(statement) == name);

  /// <summary>The helpers and the exception classes a helper names in its body, which a module that declares it declares as well.</summary>
  /// <param name="name">The name of the helper.</param>
  /// <returns>The names.</returns>
  public static IEnumerable<string> Needs(string name) {
    var names = PythonSyntaxRewriter.DescendantsAndSelf(Declaration(name)).OfType<PyName>().Select(reference => reference.Name).ToHashSet(StringComparer.Ordinal);
    names.Remove(name);
    return names;
  }

}
