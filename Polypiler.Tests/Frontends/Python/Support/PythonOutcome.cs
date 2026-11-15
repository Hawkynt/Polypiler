using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>One call of a function of a Python fixture: arguments are <see cref="long"/> for an <c>int</c>, <see cref="double"/> for a <c>float</c>, <see cref="bool"/>.</summary>
/// <param name="Function">The name of the function.</param>
/// <param name="Arguments">The arguments.</param>
internal sealed record PythonCall(string Function, object[] Arguments) {

  /// <summary>The call as Python writes it, for example <c>f(1, 2.5, True)</c>.</summary>
  public override string ToString() => $"{this.Function}({string.Join(", ", this.Arguments.Select(PythonOutcome.Literal))})";

}

/// <summary>
/// What a call did, as the oracle script prints it: <c>int 5</c>, <c>float 2.5</c> (Python's
/// <c>repr</c>), <c>bool True</c>, <c>None</c>, or <c>raise ZeroDivisionError</c>. The interpreter and
/// the code emitted from the IR are written the same way, an exception by the Python class the IR's
/// class stands for (docs/reference/exceptions.md, "Python").
/// </summary>
internal static class PythonOutcome {

  /// <summary>The Python exception every exception class of the IR's library stands for, by the class's name.</summary>
  public static string Exception(string irClass) => irClass switch {
    "DivideByZeroException" or "DivideByZeroError" => "ZeroDivisionError",
    "OverflowException" or "OverflowError" => "OverflowError",
    "ArgumentException" or "ArgumentError" => "ValueError",
    "InvalidCastException" or "InvalidCastError" => "TypeError",
    "InvalidOperationException" or "InvalidOperationError" => "AssertionError",
    "ArithmeticException" or "ArithmeticError" => "ArithmeticError",
    _ => irClass
  };

  /// <summary>
  /// What the import is expected to do for a call, given what CPython did: the same, except where the
  /// import says it differs - a complex result raises <c>ArithmeticError</c> (<c>py-complex-power</c>).
  /// An <c>int</c> is exact at every magnitude.
  /// </summary>
  public static string Expected(string python)
    => python.StartsWith("complex ", StringComparison.Ordinal) ? "raise ArithmeticError" : python;

  /// <summary>A value as the oracle prints a result.</summary>
  public static string Show(object? value) => value switch {
    null => "None",
    bool truth => "bool " + (truth ? "True" : "False"),
    long integer => "int " + integer.ToString(CultureInfo.InvariantCulture),
    BigInteger integer => "int " + integer.ToString(CultureInfo.InvariantCulture),
    double number => "float " + Repr(number),
    _ => value.GetType().Name + " " + value
  };

  /// <summary>A value as Python writes it in source.</summary>
  public static string Literal(object value) => value switch {
    bool truth => truth ? "True" : "False",
    long integer => integer.ToString(CultureInfo.InvariantCulture),
    double number => double.IsNaN(number) ? "nan" : double.IsInfinity(number) ? (number > 0 ? "inf" : "-inf") : Repr(number),
    _ => throw new ArgumentException($"{value} is no Python value of the import", nameof(value))
  };

  /// <summary>
  /// Python's <c>repr</c> of a float: the shortest digits that read back as the number, as .NET's
  /// round-trip format gives them too, laid out the way <c>float_repr_style = 'short'</c> does - plain
  /// from 1e-4 up to below 1e16, with <c>.0</c> on an integral number, otherwise with an exponent
  /// of at least two digits.
  /// </summary>
  public static string Repr(double number) {
    if (double.IsNaN(number))
      return "nan";

    if (double.IsInfinity(number))
      return number > 0 ? "inf" : "-inf";

    if (number == 0)
      return double.IsNegative(number) ? "-0.0" : "0.0";

    var sign = number < 0 ? "-" : "";
    var shortest = Math.Abs(number).ToString("R", CultureInfo.InvariantCulture);
    // The digits of the shortest form and the position of the decimal point: the value is 0.<digits> * 10^point.
    var mantissa = shortest.Split('E')[0];
    var exponent = shortest.Contains('E') ? int.Parse(shortest.Split('E')[1], CultureInfo.InvariantCulture) : 0;
    var pointAt = mantissa.IndexOf('.');
    var whole = pointAt < 0 ? mantissa : mantissa[..pointAt];
    var fraction = pointAt < 0 ? "" : mantissa[(pointAt + 1)..];
    var all = whole + fraction;
    var leading = all.Length - all.TrimStart('0').Length;
    var digits = all.TrimStart('0').TrimEnd('0');
    var point = whole.Length + exponent - leading;
    if (point > -4 && point <= 16) {
      if (point <= 0)
        return sign + "0." + new string('0', -point) + digits;

      return point >= digits.Length
        ? sign + digits + new string('0', point - digits.Length) + ".0"
        : sign + digits[..point] + "." + digits[point..];
    }

    var scientific = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
    var power = point - 1;
    return $"{sign}{scientific}e{(power < 0 ? "-" : "+")}{Math.Abs(power):00}";
  }

  /// <summary>What the interpreter does for a call of an imported module.</summary>
  public static string Interpreted(IrModule module, PythonCall call) {
    try {
      var parameters = module.Functions.First(function => function.Name == call.Function).Parameters;
      return Show(IrInterpreter.Call(module, call.Function, [.. call.Arguments.Select((argument, index) => parameters[index].Type.IsBigInt && argument is long integer ? (BigInteger)integer : argument)]));
    } catch (IrException exception) {
      return "raise " + Exception(exception.Class.Name);
    }
  }

  /// <summary>What a method compiled from the emitted C# does for a call.</summary>
  public static string Invoked(MethodInfo method, PythonCall call) {
    try {
      var parameters = method.GetParameters();
      return Show(method.Invoke(null, [.. call.Arguments.Select((argument, index) => parameters[index].ParameterType == typeof(BigInteger) && argument is long integer ? (BigInteger)integer : argument)]));
    } catch (TargetInvocationException exception) when (exception.InnerException is { } inner) {
      return "raise " + Exception(inner.GetType().Name);
    }
  }

}
