using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Backends.Go.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Go;

/// <summary>
/// The operations of the IR Go has no expression for, written with what Go has, one unexported
/// function per operation and type, emitted into the package's <c>polyrt.go</c> only where the
/// package calls it. Go's integer arithmetic never reports an overflow, so the checked sum,
/// difference, product and negation test the result (through <c>math/bits</c> on 64 bits); Go's
/// signed division wraps where the quotient of the smallest value by -1 does not fit, which the IR
/// traps on; Go leaves the conversion of a floating-point number outside the range of an integer type
/// to the implementation, so the saturating and the checked conversion test the range first; Go has
/// no checked conversion between integers, no <c>min</c> and <c>max</c> before 1.21, no sign of a
/// floating-point number that traps on a NaN and no rounding of halves upwards. Each is the operation
/// itself, for which the IR has no weaker operation to write it with. A trap is a panic with the text
/// of the IR's trap (<c>docs/reference/exceptions.md</c>).
/// </summary>
internal static class GoHelpers {

  /// <summary>The text of the panic of the overflow trap.</summary>
  public const string Overflow = "integer overflow";

  /// <summary>The folder of the repository the helpers lie in, as their origin names it.</summary>
  public const string Folder = "Polypiler.Core.Backends.Go/Helpers";

  private static readonly ConcurrentDictionary<string, GoFuncDecl> _parsed = new(StringComparer.Ordinal);

  /// <summary>The Go name of an integer type, such as <c>int32</c>.</summary>
  public static string GoName(IrType type) => type.Kind switch {
    IrTypeKind.I8 => "int8",
    IrTypeKind.U8 => "uint8",
    IrTypeKind.I16 => "int16",
    IrTypeKind.U16 => "uint16",
    IrTypeKind.I32 => "int32",
    IrTypeKind.U32 => "uint32",
    IrTypeKind.I64 => "int64",
    IrTypeKind.U64 => "uint64",
    IrTypeKind.F32 => "float32",
    IrTypeKind.F64 => "float64",
    _ => throw new ArgumentException($"The type '{type}' has no helper.", nameof(type))
  };

  /// <summary>The Go name of a type with an upper-case first letter, for the name of a helper: <c>Int32</c>.</summary>
  public static string Title(IrType type) {
    var name = GoName(type);
    return char.ToUpperInvariant(name[0]) + name[1..];
  }

  /// <summary>The name of the helper of a checked sum, difference or product.</summary>
  public static string Checked(IrBinaryOp op, IrType type) => op switch {
    IrBinaryOp.AddChecked => "plyAddChecked",
    IrBinaryOp.SubtractChecked => "plySubChecked",
    _ => "plyMulChecked"
  } + Title(type);

  /// <summary>The name of the helper of a checked negation.</summary>
  public static string Negated(IrType type) => "plyNegChecked" + Title(type);

  /// <summary>The name of the helper of the IR's division or remainder, which traps on the one quotient that does not fit.</summary>
  public static string Divided(IrBinaryOp op, IrType type) => (op == IrBinaryOp.Divide ? "plyDiv" : "plyRem") + Title(type);

  /// <summary>The name of the helper of a checked conversion between integers.</summary>
  public static string Narrowed(IrType from, IrType to) => "plyConv" + Title(from) + "To" + Title(to);

  /// <summary>The name of the helper of a conversion of a floating-point number to an integer that saturates or traps.</summary>
  public static string FromFloat(IrType to, bool trap) => "plyFloatTo" + Title(to) + (trap ? "Checked" : "Sat");

  /// <summary>The name of the helper of the smaller or larger of two integers, below Go 1.21.</summary>
  public static string Bound(bool minimum, IrType type) => (minimum ? "plyMin" : "plyMax") + Title(type);

  /// <summary>The name of the helper of the smaller of two doubles below Go 1.21, a NaN where one is.</summary>
  public const string FloatMinimum = "plyMinFloat64";

  /// <summary>The name of the helper of the larger of two doubles below Go 1.21, a NaN where one is.</summary>
  public const string FloatMaximum = "plyMaxFloat64";

  /// <summary>The name of the helper of the sign of a floating-point number.</summary>
  public const string Sign = "plySign";

  /// <summary>The name of the helper of the rounding of halves towards positive infinity.</summary>
  public const string RoundHalfUp = "plyRoundHalfUp";

  /// <summary>The declaration of a helper, read from its Go text once.</summary>
  /// <param name="name">The name, as one of the methods above made it.</param>
  /// <returns>The declaration, marked as a helper.</returns>
  /// <exception cref="ArgumentException">No helper has the name.</exception>
  public static GoFuncDecl Declaration(string name) => _parsed.GetOrAdd(name, key => {
    var declaration = (GoFuncDecl)GoSyntaxReader.ReadDeclarations(Text(key)).Single();
    return declaration with { IsHelper = true };
  });

  /// <summary>The Go text of a helper.</summary>
  /// <param name="name">The name.</param>
  /// <returns>The text.</returns>
  /// <exception cref="ArgumentException">No helper has the name.</exception>
  public static string Text(string name) {
    ArgumentNullException.ThrowIfNull(name);
    if (name == Sign)
      return """
        func plySign(x float64) int32 {
        	if x != x {
        		panic("invalid operation")
        	}
        	if x > 0 {
        		return 1
        	}
        	if x < 0 {
        		return -1
        	}
        	return 0
        }
        """;

    if (name is FloatMinimum or FloatMaximum)
      return Body(name, "a, b float64", "float64", ["if a != a {", "\treturn a", "}", "if b != b {", "\treturn b", "}", $"return math.{(name == FloatMinimum ? "Min" : "Max")}(a, b)"]);

    if (name == RoundHalfUp)
      return """
        func plyRoundHalfUp(x float64) float64 {
        	r := math.Floor(x)
        	if x-r >= 0.5 {
        		r++
        	}
        	if r == 0 {
        		return math.Copysign(0, x)
        	}
        	return r
        }
        """;

    foreach (var type in Integers) {
      var t = GoName(type);
      var title = Title(type);
      if (name == "plyAddChecked" + title)
        return Body(name, $"a, b {t}", t, AddChecked(type));
      if (name == "plySubChecked" + title)
        return Body(name, $"a, b {t}", t, SubChecked(type));
      if (name == "plyMulChecked" + title)
        return Body(name, $"a, b {t}", t, MulChecked(type));
      if (name == "plyNegChecked" + title && type.IsSigned)
        return Body(name, $"a {t}", t, [$"if a == {Lowest(type)} {{", $"\tpanic(\"{Overflow}\")", "}", "return -a"]);
      if (name == "plyDiv" + title || name == "plyRem" + title) {
        var op = name.StartsWith("plyDiv", StringComparison.Ordinal) ? "/" : "%";
        return Body(name, $"a, b {t}", t, type.IsSigned
          ? [$"if b == -1 && a == {Lowest(type)} {{", $"\tpanic(\"{Overflow}\")", "}", $"return a {op} b"]
          : [$"return a {op} b"]);
      }

      if (name == "plyMin" + title || name == "plyMax" + title)
        return Body(name, $"a, b {t}", t, [$"if b {(name.StartsWith("plyMin", StringComparison.Ordinal) ? "<" : ">")} a {{", "\treturn b", "}", "return a"]);
      if (name == "plyFloatTo" + title + "Sat")
        return Body(name, "x float64", t, Saturated(type));
      if (name == "plyFloatTo" + title + "Checked")
        return Body(name, "x float64", t, [$"if !({CheckedRange(type)}) {{", $"\tpanic(\"{Overflow}\")", "}", $"return {t}(x)"]);

      foreach (var to in Integers)
        if (name == "plyConv" + title + "To" + Title(to))
          return Body(name, $"v {t}", GoName(to), Range(type, to));
    }

    throw new ArgumentException($"No helper is named '{name}'.", nameof(name));
  }

  private static readonly IrType[] Integers = [IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64];

  private static string Body(string name, string parameters, string result, IEnumerable<string> lines)
    => $"func {name}({parameters}) {result} {{\n" + string.Concat(lines.Select(line => "\t" + line + "\n")) + "}";

  private static string Number(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);

  private static string Lowest(IrType type) => Number((BigInteger)IrOperators.Minimum(type));

  private static string Highest(IrType type) => Number((BigInteger)IrOperators.Maximum(type));

  private static string Panic => $"\tpanic(\"{Overflow}\")";

  private static IEnumerable<string> AddChecked(IrType type) => type.Kind switch {
    IrTypeKind.I64 => ["r := a + b", "if (r > a) != (b > 0) {", Panic, "}", "return r"],
    IrTypeKind.U64 => ["r, carry := bits.Add64(a, b, 0)", "if carry != 0 {", Panic, "}", "return r"],
    _ => Widened(type, "+")
  };

  private static IEnumerable<string> SubChecked(IrType type) => type.Kind switch {
    IrTypeKind.I64 => ["r := a - b", "if (r < a) != (b > 0) {", Panic, "}", "return r"],
    IrTypeKind.U64 => ["r, borrow := bits.Sub64(a, b, 0)", "if borrow != 0 {", Panic, "}", "return r"],
    _ => Widened(type, "-")
  };

  private static IEnumerable<string> MulChecked(IrType type) => type.Kind switch {
    IrTypeKind.I64 => [
      "if a == 0 || b == 0 {", "\treturn 0", "}", "r := a * b",
      "if r/b != a || (a == -1 && b == math.MinInt64) || (b == -1 && a == math.MinInt64) {", Panic, "}", "return r"
    ],
    IrTypeKind.U64 => ["hi, lo := bits.Mul64(a, b)", "if hi != 0 {", Panic, "}", "return lo"],
    _ => Widened(type, "*")
  };

  /// <summary>An operation of up to 32 bits computed exactly on 64 bits and tested against the range of the type.</summary>
  private static IEnumerable<string> Widened(IrType type, string op) {
    var wide = type.IsSigned ? "int64" : "uint64";
    var t = GoName(type);
    var test = type.IsSigned || op == "-"
      ? $"r < {Lowest(type)} || r > {Highest(type)}"
      : $"r > {Highest(type)}";
    if (!type.IsSigned && op == "-")
      return ["if b > a {", Panic, "}", "return a - b"];

    return [$"r := {wide}(a) {op} {wide}(b)", $"if {test} {{", Panic, "}", $"return {t}(r)"];
  }

  /// <summary>The checked conversion between integers: the value tested against the bounds of the target as far as the source reaches them.</summary>
  private static IEnumerable<string> Range(IrType from, IrType to) {
    var tests = new List<string>();
    if (IrOperators.Minimum(to) > IrOperators.Minimum(from))
      tests.Add($"v < {Number((BigInteger)IrOperators.Minimum(to))}");
    if (IrOperators.Maximum(to) < IrOperators.Maximum(from))
      tests.Add($"v > {Number((BigInteger)IrOperators.Maximum(to))}");

    var lines = tests.Count == 0 ? new List<string>() : [$"if {string.Join(" || ", tests)} {{", Panic, "}"];
    lines.Add($"return {GoName(to)}(v)");
    return lines;
  }

  /// <summary>
  /// The saturating conversion of a double: a NaN is 0, a number at or below the smallest value of
  /// the type is the smallest value, one at or above the largest value plus one - a power of two, which
  /// a double holds exactly - the largest; every other is truncated towards zero, which Go's conversion does.
  /// </summary>
  private static IEnumerable<string> Saturated(IrType type) {
    var t = GoName(type);
    var limit = Number((BigInteger)IrOperators.Maximum(type) + 1) + ".0";
    if (!type.IsSigned)
      return ["if x != x || x <= 0 {", "\treturn 0", "}", $"if x >= {limit} {{", $"\treturn {Highest(type)}", "}", $"return {t}(x)"];

    return [
      "if x != x {", "\treturn 0", "}",
      $"if x <= {Lowest(type)} {{", $"\treturn {Lowest(type)}", "}",
      $"if x >= {limit} {{", $"\treturn {Highest(type)}", "}",
      $"return {t}(x)"
    ];
  }

  /// <summary>
  /// The test of a checked conversion: the number lies strictly between the smallest value minus one and
  /// the largest plus one, so that its truncation is a value of the type; below 64 bits both bounds are
  /// exact, on 64 bits the smallest value itself, a power of two, is the lower bound. A NaN fails it.
  /// </summary>
  private static string CheckedRange(IrType type) {
    var limit = Number((BigInteger)IrOperators.Maximum(type) + 1) + ".0";
    if (type == IrType.I64)
      return $"x >= {Lowest(type)}.0 && x < {limit}";

    return $"x > {Number((BigInteger)IrOperators.Minimum(type) - 1)} && x < {limit}";
  }

}
