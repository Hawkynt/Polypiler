using System;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Python.Model;

/// <summary>
/// The static type of a Python value as the import knows it. <see cref="Int"/> is Python's unbounded
/// <c>int</c>, carried as the IR's arbitrary-precision integer: every operation gives the exact result.
/// </summary>
internal enum PyType {

  /// <summary>Not known yet: the inference has not reached a value of the variable.</summary>
  Unknown,

  /// <summary>A <c>bool</c>.</summary>
  Bool,

  /// <summary>An <c>int</c>: exact, of any magnitude.</summary>
  Int,

  /// <summary>A <c>float</c>: an IEEE 754 binary64 number.</summary>
  Float,

  /// <summary><c>None</c>: the result of a function that returns nothing.</summary>
  None

}

/// <summary>The rules of the types: their IR type, how two of them join and their names.</summary>
internal static class PyTypes {

  /// <summary>The IR type of a Python type.</summary>
  public static IrType Ir(PyType type) => type switch {
    PyType.Bool => IrType.Bool,
    PyType.Int => IrType.BigInt,
    PyType.Float => IrType.F64,
    PyType.None => IrType.Void,
    _ => throw new ArgumentOutOfRangeException(nameof(type), type, "An unknown type has no IR type.")
  };

  /// <summary>Whether a type is a number: <c>bool</c>, <c>int</c> or <c>float</c>.</summary>
  public static bool IsNumber(PyType type) => type is PyType.Bool or PyType.Int or PyType.Float;

  /// <summary>
  /// The type that holds the values of two types: the wider of two numbers, since a <c>bool</c> is an
  /// <c>int</c> and an <c>int</c> converts to a <c>float</c>; <see langword="null"/> where no type
  /// holds both, which is <c>None</c> with a number.
  /// </summary>
  public static PyType? Join(PyType first, PyType second) {
    if (first == PyType.Unknown || first == second)
      return second;

    if (second == PyType.Unknown)
      return first;

    if (!IsNumber(first) || !IsNumber(second))
      return null;

    return first > second ? first : second;
  }

  /// <summary>The type of a type hint, by the name Python gives it; <see langword="null"/> for any other name.</summary>
  public static PyType? OfHint(string name) => name switch {
    "int" => PyType.Int,
    "float" => PyType.Float,
    "bool" => PyType.Bool,
    "None" => PyType.None,
    _ => null
  };

  /// <summary>The name of a type as Python writes it.</summary>
  public static string Name(PyType type) => type switch {
    PyType.Bool => "bool",
    PyType.Int => "int",
    PyType.Float => "float",
    PyType.None => "None",
    _ => "unknown"
  };

}
