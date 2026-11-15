using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>
/// The operations of Python's numbers that no single IR operation computes, each a private function
/// of the module that the lowering adds where it calls one: the correctly rounded true division of
/// two integers, the floor division and modulo of floats with their sign rules and the traps of a
/// zero divisor, powers, shifts that refuse a negative count and a lost bit, the exact comparison of
/// an <c>int</c> with a <c>float</c>, and <c>int()</c> of a float. Each raises what CPython raises
/// (docs/reference/exceptions.md, "Python").
/// </summary>
/// <remarks>
/// Each is written from the Python Language Reference ("Binary arithmetic operations": <c>x == (x//y)*y
/// + (x%y)</c>, the remainder with the sign of the divisor, the quotient the floor of the exact one)
/// and the built-in types' documentation, with CPython as the oracle the differential tests compare
/// against (rung 2 of AGENTS.md: CPython's PSF-2.0 code is no source the policy admits). The float
/// remainder is the exact <c>fmod</c>, moved by the divisor where its sign differs; the quotient is
/// the exact integer <c>(a - fmod(a, b)) / b</c>, rounded by the division and brought back to the
/// nearest integer. The true division of integers is the correctly rounded quotient, found by long
/// division to 55 significant bits and a sticky bit, so that the one rounding to binary64 is the only one.
/// </remarks>
internal sealed partial class PythonHelpers(IReadOnlySet<string> taken) {

  /// <summary>The comparison a call of <see cref="CompareIntFloat"/> makes, by the code it passes.</summary>
  public static readonly IReadOnlyDictionary<string, int> Comparisons = new Dictionary<string, int> {
    ["Lt"] = 0, ["LtE"] = 1, ["Gt"] = 2, ["GtE"] = 3, ["Eq"] = 4, ["NotEq"] = 5
  };

  private readonly Dictionary<string, IrFunction> _made = new(StringComparer.Ordinal);
  private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

  /// <summary>The helpers called so far, in the order they were first called.</summary>
  public IReadOnlyList<IrFunction> Functions => [.. this._made.Values];

  /// <summary><c>a / b</c> of two <c>int</c>: the correctly rounded quotient; <c>ZeroDivisionError</c>, <c>OverflowError</c> beyond the largest float.</summary>
  public IrCall TrueDivideInt(IrExpr a, IrExpr b) => this.Call("PyTrueDivideInt", IrType.F64, [a, b], this.MakeTrueDivideInt);

  /// <summary><c>a / b</c> of two <c>float</c>; <c>ZeroDivisionError</c>.</summary>
  public IrCall TrueDivideFloat(IrExpr a, IrExpr b) => this.Call("PyTrueDivideFloat", IrType.F64, [a, b], this.MakeTrueDivideFloat);

  /// <summary><c>a // b</c> of two <c>float</c>; <c>ZeroDivisionError</c>.</summary>
  public IrCall FloorDivideFloat(IrExpr a, IrExpr b) => this.Call("PyFloorDivideFloat", IrType.F64, [a, b], this.MakeFloorDivideFloat);

  /// <summary><c>a % b</c> of two <c>float</c>, with the sign of the divisor; <c>ZeroDivisionError</c>.</summary>
  public IrCall ModuloFloat(IrExpr a, IrExpr b) => this.Call("PyModuloFloat", IrType.F64, [a, b], this.MakeModuloFloat);

  /// <summary><c>a // b</c> of two <c>int</c>, the quotient rounded towards negative infinity; <c>ZeroDivisionError</c>.</summary>
  public IrCall FloorDivideInt(IrExpr a, IrExpr b) => this.Call("PyFloorDivideInt", IrType.BigInt, [a, b], this.MakeFloorDivideInt);

  /// <summary><c>a % b</c> of two <c>int</c>, with the sign of the divisor; <c>ZeroDivisionError</c>.</summary>
  public IrCall ModuloInt(IrExpr a, IrExpr b) => this.Call("PyModuloInt", IrType.BigInt, [a, b], this.MakeModuloInt);

  /// <summary><c>a ** b</c> of two <c>int</c>, <c>b</c> not negative.</summary>
  public IrCall PowerInt(IrExpr a, IrExpr b) => this.Call("PyPowerInt", IrType.BigInt, [a, b], this.MakePowerInt);

  /// <summary><c>a ** b</c> of two <c>float</c>; <c>ZeroDivisionError</c>, <c>OverflowError</c>, and a trap where CPython gives a complex.</summary>
  public IrCall PowerFloat(IrExpr a, IrExpr b) => this.Call("PyPowerFloat", IrType.F64, [a, b], this.MakePowerFloat);

  /// <summary><c>a &lt;&lt; n</c>: <c>ValueError</c> for a negative count, <c>OverflowError</c> for a count beyond 2**31 - 1.</summary>
  public IrCall ShiftLeft(IrExpr a, IrExpr n) => this.Call("PyShiftLeft", IrType.BigInt, [a, n], this.MakeShiftLeft);

  /// <summary><c>a &gt;&gt; n</c>, arithmetic, for any count; <c>ValueError</c> for a negative one.</summary>
  public IrCall ShiftRight(IrExpr a, IrExpr n) => this.Call("PyShiftRight", IrType.BigInt, [a, n], this.MakeShiftRight);

  /// <summary><c>int(x)</c> of a <c>float</c>: toward zero; <c>ValueError</c> for a NaN, <c>OverflowError</c> for an infinity.</summary>
  public IrCall IntOfFloat(IrExpr x) => this.Call("PyIntOfFloat", IrType.BigInt, [x], this.MakeIntOfFloat);

  /// <summary><c>float(a)</c> of an <c>int</c>: the nearest float; <c>OverflowError</c> beyond the largest one.</summary>
  public IrCall FloatOfInt(IrExpr a) => this.Call("PyFloatOfInt", IrType.F64, [a], this.MakeFloatOfInt);

  /// <summary><c>abs(a)</c> of an <c>int</c>.</summary>
  public IrCall AbsInt(IrExpr a) => this.Call("PyAbsInt", IrType.BigInt, [a], this.MakeAbsInt);

  /// <summary>An exact comparison of an <c>int</c> with a <c>float</c>, by the code of <see cref="Comparisons"/>.</summary>
  public IrCall CompareIntFloat(IrExpr a, IrExpr b, int code) => this.Call("PyCompareIntFloat", IrType.Bool, [a, b, Int32(code)], this.MakeCompareIntFloat);

  /// <summary>An exact comparison of a <c>float</c> with an <c>int</c>, by the code of <see cref="Comparisons"/>.</summary>
  public IrCall CompareFloatInt(IrExpr a, IrExpr b, int code) => this.Call("PyCompareFloatInt", IrType.Bool, [a, b, Int32(code)], this.MakeCompareFloatInt);

  private IrCall Call(string key, IrType type, IReadOnlyList<IrExpr> arguments, Func<string, IrFunction> make) {
    if (!this._names.TryGetValue(key, out var name)) {
      name = key;
      for (var suffix = 1; taken.Contains(name); ++suffix)
        name = $"{key}_{suffix}";

      this._names[key] = name;
      this._made[key] = make(name);
    }

    // An int goes in as the parameter holds it: exact, or as the i64 of an index or a count.
    var parameters = this._made[key].Parameters;
    return new IrCall(name, type, [.. arguments.Select((argument, index) => index < parameters.Count ? Fit(argument, parameters[index].Type) : argument)]);
  }

  private static IrFunction Function(string name, IrType returns, IReadOnlyList<IrLocal> parameters, IReadOnlyList<IrLocal> locals, params IReadOnlyList<IrStmt> body)
    => new(name, false, returns, parameters, locals, Block(body));

  private static IrLocal Local(string name, IrType type) => new(name, type);

  private IrFunction MakeTrueDivideInt(string name) {
    var (a, b) = (Local("a", IrType.BigInt), Local("b", IrType.BigInt));
    var (n, d, e, q, s, sticky) = (Local("n", IrType.BigInt), Local("d", IrType.BigInt), Local("e", IrType.I64), Local("q", IrType.I64), Local("s", IrType.I64), Local("sticky", IrType.Bool));
    var (mantissa, rest, magnitude) = (Local("mantissa", IrType.I64), Local("rest", IrType.I64), Local("magnitude", IrType.F64));
    IrExpr Within(IrLocal x) => Binary(IrBinaryOp.LogicalAnd, Binary(IrBinaryOp.GreaterOrEqual, Ref(x), Big(-(1L << 53))), Binary(IrBinaryOp.LessOrEqual, Ref(x), Big(1L << 53)));
    IrExpr Magnitude(IrLocal x) => Choose(Binary(IrBinaryOp.Less, Ref(x), Big(0)), new IrUnary(IrUnaryOp.Negate, Ref(x)), Ref(x));
    IrExpr Shifted(IrLocal x, int count) => Binary(IrBinaryOp.ShiftLeft, Ref(x), Int32(count));
    IrBlock Step(IrLocal x, int count, long exponent) => Block(Assign(x, Shifted(x, count)), Assign(e, Binary(IrBinaryOp.Add, Ref(e), Int(exponent))));
    IrExpr Mask(IrExpr count) => Binary(IrBinaryOp.Subtract, Binary(IrBinaryOp.ShiftLeft, Int(1), Convert(IrType.I32, count)), Int(1));
    return Function(name, IrType.F64, [a, b], [n, d, e, q, s, sticky, mantissa, rest, magnitude],
      If(Equal(Ref(b), Big(0)), Raise(IrExceptionClass.DivideByZeroException, "division by zero")),
      // Two integers a binary64 number holds exactly divide with the one rounding of the division; so does a zero, whose sign is the quotient's.
      If(Binary(IrBinaryOp.LogicalAnd, Within(a), Within(b)), Return(Binary(IrBinaryOp.Divide, Convert(IrType.F64, Ref(a)), Convert(IrType.F64, Ref(b))))),
      If(Equal(Ref(a), Big(0)), Return(Choose(Binary(IrBinaryOp.Less, Ref(b), Big(0)), Float(-0.0), Float(0)))),
      new IrDeclare(n, Magnitude(a)),
      new IrDeclare(d, Magnitude(b)),
      new IrDeclare(e, Int(0)),
      // The quotient n / d * 2^e is brought into [2^54, 2^55): 53 bits, a rounding bit and one more; divisor and dividend move by 64 bits first, then by one.
      new IrWhile(Binary(IrBinaryOp.GreaterOrEqual, Ref(n), Shifted(d, 55 + 64)), Step(d, 64, 64)),
      new IrWhile(Binary(IrBinaryOp.GreaterOrEqual, Ref(n), Shifted(d, 55)), Step(d, 1, 1)),
      new IrWhile(Binary(IrBinaryOp.Less, Shifted(n, 64), Shifted(d, 54)), Step(n, 64, -64)),
      new IrWhile(Binary(IrBinaryOp.Less, Ref(n), Shifted(d, 54)), Step(n, 1, -1)),
      new IrDeclare(q, Convert(IrType.I64, Binary(IrBinaryOp.Divide, Ref(n), Ref(d)))),
      new IrDeclare(sticky, Binary(IrBinaryOp.NotEqual, Binary(IrBinaryOp.Remainder, Ref(n), Ref(d)), Big(0))),
      new IrDeclare(s, Int(0)),
      // Below the normal numbers the last bit kept weighs 2^-1074: the bits beneath it join the sticky bit.
      If(Binary(IrBinaryOp.Less, Ref(e), Int(-1076)),
        Assign(s, Binary(IrBinaryOp.Subtract, Int(-1076), Ref(e))),
        new IrIf(Binary(IrBinaryOp.GreaterOrEqual, Ref(s), Int(56)), Block(
          Assign(sticky, Binary(IrBinaryOp.LogicalOr, Ref(sticky), Binary(IrBinaryOp.NotEqual, Ref(q), Int(0)))),
          Assign(q, Int(0))), Block(
          Assign(sticky, Binary(IrBinaryOp.LogicalOr, Ref(sticky), Binary(IrBinaryOp.NotEqual, Binary(IrBinaryOp.And, Ref(q), Mask(Ref(s))), Int(0)))),
          Assign(q, Binary(IrBinaryOp.ShiftRight, Ref(q), Convert(IrType.I32, Ref(s)))))),
        Assign(e, Int(-1076))),
      // Rounded to nearest, ties to even, by the two bits below the last one kept and the sticky bit.
      new IrDeclare(mantissa, Binary(IrBinaryOp.ShiftRight, Ref(q), Int32(2))),
      new IrDeclare(rest, Binary(IrBinaryOp.And, Ref(q), Int(3))),
      If(Binary(IrBinaryOp.LogicalOr, Binary(IrBinaryOp.Greater, Ref(rest), Int(2)), Binary(IrBinaryOp.LogicalAnd, Equal(Ref(rest), Int(2)),
          Binary(IrBinaryOp.LogicalOr, Ref(sticky), Equal(Binary(IrBinaryOp.And, Ref(mantissa), Int(1)), Int(1))))),
        Assign(mantissa, Binary(IrBinaryOp.Add, Ref(mantissa), Int(1)))),
      // Scaled by powers of two, which is exact: the number lies on the grid of its binade, or overflows.
      new IrDeclare(magnitude, Convert(IrType.F64, Ref(mantissa))),
      Assign(e, Binary(IrBinaryOp.Add, Ref(e), Int(2))),
      new IrWhile(Binary(IrBinaryOp.LogicalAnd, Binary(IrBinaryOp.Greater, Ref(e), Int(0)), Intrinsic(IrIntrinsicOp.IsFinite, IrType.Bool, Ref(magnitude))), Block(
        Assign(magnitude, Binary(IrBinaryOp.Multiply, Ref(magnitude), Float(2))),
        Assign(e, Binary(IrBinaryOp.Subtract, Ref(e), Int(1))))),
      new IrWhile(Binary(IrBinaryOp.Less, Ref(e), Int(0)), Block(
        Assign(magnitude, Binary(IrBinaryOp.Multiply, Ref(magnitude), Float(0.5))),
        Assign(e, Binary(IrBinaryOp.Add, Ref(e), Int(1))))),
      If(Intrinsic(IrIntrinsicOp.IsInfinity, IrType.Bool, Ref(magnitude)), Raise(IrExceptionClass.OverflowException, "integer division result too large for a float")),
      Return(Choose(Binary(IrBinaryOp.NotEqual, Binary(IrBinaryOp.Less, Ref(a), Big(0)), Binary(IrBinaryOp.Less, Ref(b), Big(0))),
        new IrUnary(IrUnaryOp.Negate, Ref(magnitude)), Ref(magnitude))));
  }

  private IrFunction MakeTrueDivideFloat(string name) {
    var (a, b) = (Local("a", IrType.F64), Local("b", IrType.F64));
    return Function(name, IrType.F64, [a, b], [],
      If(Equal(Ref(b), Float(0)), Raise(IrExceptionClass.DivideByZeroException, "float division by zero")),
      Return(Binary(IrBinaryOp.Divide, Ref(a), Ref(b))));
  }

  private IrFunction MakeFloorDivideFloat(string name) {
    var (a, b) = (Local("a", IrType.F64), Local("b", IrType.F64));
    var (mod, div, floor) = (Local("mod", IrType.F64), Local("div", IrType.F64), Local("floordiv", IrType.F64));
    return Function(name, IrType.F64, [a, b], [mod, div, floor],
      If(Equal(Ref(b), Float(0)), Raise(IrExceptionClass.DivideByZeroException, "float floor division by zero")),
      new IrDeclare(mod, Binary(IrBinaryOp.Remainder, Ref(a), Ref(b))),
      new IrDeclare(div, Binary(IrBinaryOp.Divide, Binary(IrBinaryOp.Subtract, Ref(a), Ref(mod)), Ref(b))),
      If(Binary(IrBinaryOp.LogicalAnd, Binary(IrBinaryOp.NotEqual, Ref(mod), Float(0)), Binary(IrBinaryOp.NotEqual, Binary(IrBinaryOp.Less, Ref(b), Float(0)), Binary(IrBinaryOp.Less, Ref(mod), Float(0)))),
        Assign(div, Binary(IrBinaryOp.Subtract, Ref(div), Float(1)))),
      If(Equal(Ref(div), Float(0)), Return(Intrinsic(IrIntrinsicOp.CopySign, IrType.F64, Float(0), Binary(IrBinaryOp.Divide, Ref(a), Ref(b))))),
      new IrDeclare(floor, Intrinsic(IrIntrinsicOp.Floor, IrType.F64, Ref(div))),
      If(Binary(IrBinaryOp.Greater, Binary(IrBinaryOp.Subtract, Ref(div), Ref(floor)), Float(0.5)), Assign(floor, Binary(IrBinaryOp.Add, Ref(floor), Float(1)))),
      Return(Ref(floor)));
  }

  private IrFunction MakeModuloFloat(string name) {
    var (a, b) = (Local("a", IrType.F64), Local("b", IrType.F64));
    var mod = Local("mod", IrType.F64);
    return Function(name, IrType.F64, [a, b], [mod],
      If(Equal(Ref(b), Float(0)), Raise(IrExceptionClass.DivideByZeroException, "float modulo by zero")),
      new IrDeclare(mod, Binary(IrBinaryOp.Remainder, Ref(a), Ref(b))),
      If(Equal(Ref(mod), Float(0)), Return(Intrinsic(IrIntrinsicOp.CopySign, IrType.F64, Float(0), Ref(b)))),
      If(Binary(IrBinaryOp.NotEqual, Binary(IrBinaryOp.Less, Ref(b), Float(0)), Binary(IrBinaryOp.Less, Ref(mod), Float(0))),
        Return(Binary(IrBinaryOp.Add, Ref(mod), Ref(b)))),
      Return(Ref(mod)));
  }

  /// <summary>Whether a truncating remainder differs in sign from the divisor: then the floor division's quotient is one lower and its remainder moved by the divisor.</summary>
  private static IrExpr Differs(IrLocal remainder, IrLocal divisor)
    => Binary(IrBinaryOp.LogicalAnd, Binary(IrBinaryOp.NotEqual, Ref(remainder), Big(0)), Binary(IrBinaryOp.NotEqual, Binary(IrBinaryOp.Less, Ref(remainder), Big(0)), Binary(IrBinaryOp.Less, Ref(divisor), Big(0))));

  private IrFunction MakeFloorDivideInt(string name) {
    var (a, b) = (Local("a", IrType.BigInt), Local("b", IrType.BigInt));
    var remainder = Local("remainder", IrType.BigInt);
    return Function(name, IrType.BigInt, [a, b], [remainder],
      If(Equal(Ref(b), Big(0)), Raise(IrExceptionClass.DivideByZeroException, "division by zero")),
      new IrDeclare(remainder, Binary(IrBinaryOp.Remainder, Ref(a), Ref(b))),
      If(Differs(remainder, b), Return(Binary(IrBinaryOp.Subtract, Binary(IrBinaryOp.Divide, Ref(a), Ref(b)), Big(1)))),
      Return(Binary(IrBinaryOp.Divide, Ref(a), Ref(b))));
  }

  private IrFunction MakeModuloInt(string name) {
    var (a, b) = (Local("a", IrType.BigInt), Local("b", IrType.BigInt));
    var remainder = Local("remainder", IrType.BigInt);
    return Function(name, IrType.BigInt, [a, b], [remainder],
      If(Equal(Ref(b), Big(0)), Raise(IrExceptionClass.DivideByZeroException, "division by zero")),
      new IrDeclare(remainder, Binary(IrBinaryOp.Remainder, Ref(a), Ref(b))),
      If(Differs(remainder, b), Return(Binary(IrBinaryOp.Add, Ref(remainder), Ref(b)))),
      Return(Ref(remainder)));
  }

  private IrFunction MakePowerInt(string name) {
    var (a, b) = (Local("a", IrType.BigInt), Local("b", IrType.BigInt));
    var result = Local("result", IrType.BigInt);
    return Function(name, IrType.BigInt, [a, b], [result],
      If(Binary(IrBinaryOp.Less, Ref(b), Big(0)), Raise(IrExceptionClass.ArgumentException, "negative exponent of an int whose sign is not known")),
      new IrDeclare(result, Big(1)),
      // Square and multiply; a square is only made where a higher bit of the exponent needs it.
      new IrWhile(Binary(IrBinaryOp.Greater, Ref(b), Big(0)), Block(
        If(Binary(IrBinaryOp.NotEqual, Binary(IrBinaryOp.And, Ref(b), Big(1)), Big(0)), Assign(result, Binary(IrBinaryOp.Multiply, Ref(result), Ref(a)))),
        Assign(b, Binary(IrBinaryOp.ShiftRight, Ref(b), Int32(1))),
        If(Binary(IrBinaryOp.Greater, Ref(b), Big(0)), Assign(a, Binary(IrBinaryOp.Multiply, Ref(a), Ref(a)))))),
      Return(Ref(result)));
  }

  private IrFunction MakePowerFloat(string name) {
    var (a, b) = (Local("a", IrType.F64), Local("b", IrType.F64));
    var result = Local("result", IrType.F64);
    IrExpr Finite(IrLocal x) => Intrinsic(IrIntrinsicOp.IsFinite, IrType.Bool, Ref(x));
    return Function(name, IrType.F64, [a, b], [result],
      // CPython answers an infinite exponent before it looks at a zero base: 0.0 ** -inf is inf.
      If(Binary(IrBinaryOp.LogicalAnd, Equal(Ref(a), Float(0)), Binary(IrBinaryOp.LogicalAnd, Binary(IrBinaryOp.Less, Ref(b), Float(0)), Finite(b))),
        Raise(IrExceptionClass.DivideByZeroException, "0.0 cannot be raised to a negative power")),
      // A negative number to a fractional power is a complex in CPython, which the import has no type
      // for; CPython raises OverflowError where the magnitude of that complex overflows.
      If(Binary(IrBinaryOp.LogicalAnd, Binary(IrBinaryOp.LogicalAnd, Binary(IrBinaryOp.Less, Ref(a), Float(0)), Finite(a)),
          Binary(IrBinaryOp.LogicalAnd, Finite(b), Binary(IrBinaryOp.NotEqual, Intrinsic(IrIntrinsicOp.Floor, IrType.F64, Ref(b)), Ref(b)))),
        If(Intrinsic(IrIntrinsicOp.IsInfinity, IrType.Bool, Intrinsic(IrIntrinsicOp.Pow, IrType.F64, new IrUnary(IrUnaryOp.Negate, Ref(a)), Ref(b))),
          Raise(IrExceptionClass.OverflowException, "complex exponentiation")),
        Raise(IrExceptionClass.ArithmeticException, "a negative number to a fractional power is complex")),
      new IrDeclare(result, Intrinsic(IrIntrinsicOp.Pow, IrType.F64, Ref(a), Ref(b))),
      If(Binary(IrBinaryOp.LogicalAnd, Intrinsic(IrIntrinsicOp.IsInfinity, IrType.Bool, Ref(result)), Binary(IrBinaryOp.LogicalAnd, Finite(a), Finite(b))),
        Raise(IrExceptionClass.OverflowException, "Numerical result out of range")),
      Return(Ref(result)));
  }

  private IrFunction MakeShiftLeft(string name) {
    var (a, n) = (Local("a", IrType.BigInt), Local("n", IrType.BigInt));
    return Function(name, IrType.BigInt, [a, n], [],
      If(Binary(IrBinaryOp.Less, Ref(n), Big(0)), Raise(IrExceptionClass.ArgumentException, "negative shift count")),
      If(Equal(Ref(a), Big(0)), Return(Big(0))),
      If(Binary(IrBinaryOp.Greater, Ref(n), Big(int.MaxValue)), Raise(IrExceptionClass.OverflowException, "too many digits in integer")),
      Return(Binary(IrBinaryOp.ShiftLeft, Ref(a), Convert(IrType.I32, Ref(n)))));
  }

  private IrFunction MakeShiftRight(string name) {
    var (a, n) = (Local("a", IrType.BigInt), Local("n", IrType.BigInt));
    return Function(name, IrType.BigInt, [a, n], [],
      If(Binary(IrBinaryOp.Less, Ref(n), Big(0)), Raise(IrExceptionClass.ArgumentException, "negative shift count")),
      // A count beyond 2^31 - 1 shifts out every bit an int of the import can have.
      If(Binary(IrBinaryOp.Greater, Ref(n), Big(int.MaxValue)), Return(Choose(Binary(IrBinaryOp.Less, Ref(a), Big(0)), Big(-1), Big(0)))),
      Return(Binary(IrBinaryOp.ShiftRight, Ref(a), Convert(IrType.I32, Ref(n)))));
  }

  private IrFunction MakeIntOfFloat(string name) {
    var x = Local("x", IrType.F64);
    return Function(name, IrType.BigInt, [x], [],
      If(Intrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, Ref(x)), Raise(IrExceptionClass.ArgumentException, "cannot convert float NaN to integer")),
      If(Intrinsic(IrIntrinsicOp.IsInfinity, IrType.Bool, Ref(x)), Raise(IrExceptionClass.OverflowException, "cannot convert float infinity to integer")),
      Return(new IrConvert(IrType.BigInt, Ref(x), IrConversion.Trap)));
  }

  private IrFunction MakeFloatOfInt(string name) {
    var a = Local("a", IrType.BigInt);
    var result = Local("result", IrType.F64);
    return Function(name, IrType.F64, [a], [result],
      new IrDeclare(result, Convert(IrType.F64, Ref(a))),
      If(Intrinsic(IrIntrinsicOp.IsInfinity, IrType.Bool, Ref(result)), Raise(IrExceptionClass.OverflowException, "int too large to convert to float")),
      Return(Ref(result)));
  }

  private IrFunction MakeAbsInt(string name) {
    var a = Local("a", IrType.BigInt);
    return Function(name, IrType.BigInt, [a], [],
      If(Binary(IrBinaryOp.Less, Ref(a), Big(0)), Return(new IrUnary(IrUnaryOp.Negate, Ref(a)))),
      Return(Ref(a)));
  }

  private IrFunction MakeCompareIntFloat(string name) {
    var (a, b, op) = (Local("a", IrType.BigInt), Local("b", IrType.F64), Local("op", IrType.I32));
    var (order, truncated, whole) = (Local("order", IrType.I32), Local("truncated", IrType.F64), Local("whole", IrType.BigInt));
    IrExpr Is(int code) => Equal(Ref(op), Int32(code));
    IrExpr Sign(IrBinaryOp op, IrLocal left, IrLocal right) => Choose(Binary(op, Ref(left), Ref(right)), Int32(1), Int32(0));
    // An infinity is beyond every int; the integral part of a finite float is an int exactly, which
    // decides unless it equals the int, and then the fraction decides.
    var finite = Block(
      new IrDeclare(truncated, Intrinsic(IrIntrinsicOp.Truncate, IrType.F64, Ref(b))),
      new IrDeclare(whole, new IrConvert(IrType.BigInt, Ref(truncated), IrConversion.Trap)),
      Assign(order, Binary(IrBinaryOp.Subtract, Sign(IrBinaryOp.Greater, a, whole), Sign(IrBinaryOp.Less, a, whole))),
      If(Equal(Ref(order), Int32(0)), Assign(order, Binary(IrBinaryOp.Subtract, Sign(IrBinaryOp.Less, b, truncated), Sign(IrBinaryOp.Greater, b, truncated)))));
    var infinite = new IrIf(Intrinsic(IrIntrinsicOp.IsInfinity, IrType.Bool, Ref(b)),
      Block(Assign(order, Choose(Binary(IrBinaryOp.Greater, Ref(b), Float(0)), Int32(-1), Int32(1)))), finite);
    var result = Choose(Is(4), Equal(Ref(order), Int32(0)), Binary(IrBinaryOp.NotEqual, Ref(order), Int32(0)));
    result = Choose(Is(3), Binary(IrBinaryOp.GreaterOrEqual, Ref(order), Int32(0)), result);
    result = Choose(Is(2), Binary(IrBinaryOp.Greater, Ref(order), Int32(0)), result);
    result = Choose(Is(1), Binary(IrBinaryOp.LessOrEqual, Ref(order), Int32(0)), result);
    result = Choose(Is(0), Binary(IrBinaryOp.Less, Ref(order), Int32(0)), result);
    return Function(name, IrType.Bool, [a, b, op], [order, truncated, whole],
      If(Intrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, Ref(b)), Return(Is(5))),
      new IrDeclare(order, Int32(0)),
      infinite,
      Return(result));
  }

  private IrFunction MakeCompareFloatInt(string name) {
    var (a, b, op) = (Local("a", IrType.F64), Local("b", IrType.BigInt), Local("op", IrType.I32));
    var compare = this.Call("PyCompareIntFloat", IrType.Bool, [], this.MakeCompareIntFloat).Function;
    // The operands are evaluated in the caller's order; the comparison is the mirrored one of the swapped operands.
    return Function(name, IrType.Bool, [a, b, op], [],
      Return(new IrCall(compare, IrType.Bool, [Ref(b), Ref(a), Choose(Equal(Ref(op), Int32(0)), Int32(2),
        Choose(Equal(Ref(op), Int32(1)), Int32(3), Choose(Equal(Ref(op), Int32(2)), Int32(0), Choose(Equal(Ref(op), Int32(3)), Int32(1), Ref(op)))))])));
  }

  /// <summary>The names every helper may take, so that the lowering keeps them free for its own functions.</summary>
  public static IEnumerable<string> Keys => ["PyModuloInt", "PyFloorDivideInt", "PyFloatOfInt", "PyTrueDivideInt", "PyTrueDivideFloat", "PyFloorDivideFloat", "PyModuloFloat", "PyPowerInt", "PyPowerFloat",
    "PyShiftLeft", "PyShiftRight", "PyIntOfFloat", "PyAbsInt", "PyCompareIntFloat", "PyCompareFloatInt"];

}
