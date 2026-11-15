using System;
using System.Collections.Generic;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>
/// The names of the standard library the import knows, by the module that defines them: what an
/// import may bind, and what a call of one or a read of a constant is. A name is bound by the import
/// statement that names it, never by its bare text, so a function of the user's that is called
/// <c>sqrt</c> is the user's (spec/languages/python/callmap.yaml lists the same rows).
/// </summary>
internal static class LibraryCallMap {

  private static readonly Dictionary<string, HashSet<string>> _modules = new() {
    ["math"] = ["sqrt", "isqrt", "floor", "ceil", "trunc", "fabs", "isnan", "isinf", "isfinite", "copysign", "gcd", "pi", "e", "inf", "nan", "tau"],
    ["struct"] = ["pack", "unpack", "unpack_from", "calcsize", "error"],
    ["dataclasses"] = ["dataclass", "field"],
    ["enum"] = ["Enum", "IntEnum"],
    ["typing"] = ["Optional", "NamedTuple", "Iterator", "Generator", "Iterable", "List", "Tuple", "Union", "Final", "ClassVar", "Generic", "TypeVar", "Protocol"],
    ["collections.abc"] = ["Iterator", "Generator", "Iterable"]
  };

  /// <summary>Whether a module is one of the standard library the import knows.</summary>
  public static bool IsKnownModule(string module) => _modules.ContainsKey(module);

  /// <summary>Whether a module of the standard library defines a name the import knows.</summary>
  public static bool Has(string module, string name) => _modules.TryGetValue(module, out var names) && names.Contains(name);

  /// <summary>The functions of <c>math</c> the import calls, with the type of their result; <see langword="null"/> for a constant.</summary>
  public static PyType? MathResult(string name) => name switch {
    "sqrt" or "fabs" or "copysign" => PyType.Float,
    "isqrt" or "floor" or "ceil" or "trunc" or "gcd" => PyType.Int,
    "isnan" or "isinf" or "isfinite" => PyType.Bool,
    _ => null
  };

  /// <summary>The value of a constant of the standard library: <c>math.pi</c> and its kin; <see langword="null"/> for any other name.</summary>
  public static (PyType Type, IrExpr Value)? Constant(ImportBinding binding, string? attribute) {
    var (module, name) = binding.Name is null ? (binding.Module, attribute) : (binding.Module, binding.Name);
    if (binding.User is not null || module != "math")
      return null;

    double? value = name switch {
      "pi" => Math.PI,
      "e" => Math.E,
      "tau" => Math.Tau,
      "inf" => double.PositiveInfinity,
      // CPython's nan is the quiet NaN with the sign bit clear; .NET's double.NaN has it set.
      "nan" => BitConverter.Int64BitsToDouble(0x7FF8000000000000),
      _ => null
    };
    return value is { } number ? (PyType.Float, Ir.Float(number)) : null;
  }

}
