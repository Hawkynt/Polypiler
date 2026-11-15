using System;
using System.Collections.Generic;
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

  /// <summary><c>a / b</c> of two <c>int</c>: the correctly rounded quotient; <c>ZeroDivisionError</c>.</summary>
  public IrCall TrueDivideInt(IrExpr a, IrExpr b) => this.Call("PyTrueDivideInt", IrType.F64, [a, b], this.MakeTrueDivideInt);

  /// <summary><c>a / b</c> of two <c>float</c>; <c>ZeroDivisionError</c>.</summary>
  public IrCall TrueDivideFloat(IrExpr a, IrExpr b) => this.Call("PyTrueDivideFloat", IrType.F64, [a, b], this.MakeTrueDivideFloat);

  /// <summary><c>a // b</c> of two <c>float</c>; <c>ZeroDivisionError</c>.</summary>
  public IrCall FloorDivideFloat(IrExpr a, IrExpr b) => this.Call("PyFloorDivideFloat", IrType.F64, [a, b], this.MakeFloorDivideFloat);

  /// <summary><c>a % b</c> of two <c>float</c>, with the sign of the divisor; <c>ZeroDivisionError</c>.</summary>
  public IrCall ModuloFloat(IrExpr a, IrExpr b) => this.Call("PyModuloFloat", IrType.F64, [a, b], this.MakeModuloFloat);

  /// <summary><c>a % b</c> of two <c>int</c>, with the sign of the divisor; <c>ZeroDivisionError</c>. Of any number and -1 it is 0, also of the smallest.</summary>
  public IrCall ModuloInt(IrExpr a, IrExpr b) => this.Call("PyModuloInt", IrType.I64, [a, b], this.MakeModuloInt);

  /// <summary><c>a ** b</c> of two <c>int</c>, <c>b</c> not negative; <c>OverflowError</c> beyond 64 bits.</summary>
  public IrCall PowerInt(IrExpr a, IrExpr b) => this.Call("PyPowerInt", IrType.I64, [a, b], this.MakePowerInt);

  /// <summary><c>a ** b</c> of two <c>float</c>; <c>ZeroDivisionError</c>, <c>OverflowError</c>, and a trap where CPython gives a complex.</summary>
  public IrCall PowerFloat(IrExpr a, IrExpr b) => this.Call("PyPowerFloat", IrType.F64, [a, b], this.MakePowerFloat);

  /// <summary><c>a &lt;&lt; n</c>: <c>ValueError</c> for a negative count, <c>OverflowError</c> for a lost bit.</summary>
  public IrCall ShiftLeft(IrExpr a, IrExpr n) => this.Call("PyShiftLeft", IrType.I64, [a, n], this.MakeShiftLeft);

  /// <summary><c>a &gt;&gt; n</c>, arithmetic, for any count; <c>ValueError</c> for a negative one.</summary>
  public IrCall ShiftRight(IrExpr a, IrExpr n) => this.Call("PyShiftRight", IrType.I64, [a, n], this.MakeShiftRight);

  /// <summary><c>int(x)</c> of a <c>float</c>: toward zero; <c>ValueError</c> for a NaN, <c>OverflowError</c> for an infinity or beyond 64 bits.</summary>
  public IrCall IntOfFloat(IrExpr x) => this.Call("PyIntOfFloat", IrType.I64, [x], this.MakeIntOfFloat);

  /// <summary><c>abs(a)</c> of an <c>int</c>; <c>OverflowError</c> for the one value whose magnitude needs 64 bits.</summary>
  public IrCall AbsInt(IrExpr a) => this.Call("PyAbsInt", IrType.I64, [a], this.MakeAbsInt);

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

    return new IrCall(name, type, arguments);
  }

  private static IrFunction Function(string name, IrType returns, IReadOnlyList<IrLocal> parameters, IReadOnlyList<IrLocal> locals, params IReadOnlyList<IrStmt> body)
    => new(name, false, returns, parameters, locals, Block(body));

  private static IrLocal Local(string name, IrType type) => new(name, type);

  private IrFunction MakeTrueDivideInt(string name) {
    var (a, b) = (Local("a", IrType.I64), Local("b", IrType.I64));
    var (n, d, q, r, e, sticky) = (Local("n", IrType.U64), Local("d", IrType.U64), Local("q", IrType.U64), Local("r", IrType.U64), Local("e", IrType.I32), Local("sticky", IrType.U64));
    const long exact = 1L << 53;
    IrExpr Within(IrLocal x) => Binary(IrBinaryOp.LogicalAnd, Binary(IrBinaryOp.GreaterOrEqual, Ref(x), Int(-exact)), Binary(IrBinaryOp.LessOrEqual, Ref(x), Int(exact)));
    IrExpr Magnitude(IrLocal x) => Choose(Binary(IrBinaryOp.Less, Ref(x), Int(0)), Binary(IrBinaryOp.Subtract, UInt(0), Convert(IrType.U64, Ref(x))), Convert(IrType.U64, Ref(x)));
    var magnitude = Local("magnitude", IrType.F64);
    return Function(name, IrType.F64, [a, b], [n, d, q, r, e, sticky, magnitude],
      If(Equal(Ref(b), Int(0)), Raise(IrExceptionClass.DivideByZeroException, "division by zero")),
      // Two integers a binary64 number holds exactly divide with the one rounding of the division; so does a zero, whose sign is the quotient's.
      If(Binary(IrBinaryOp.LogicalOr, Equal(Ref(a), Int(0)), Binary(IrBinaryOp.LogicalAnd, Within(a), Within(b))), Return(Binary(IrBinaryOp.Divide, Convert(IrType.F64, Ref(a)), Convert(IrType.F64, Ref(b))))),
      new IrDeclare(n, Magnitude(a)),
      new IrDeclare(d, Magnitude(b)),
      new IrDeclare(q, Binary(IrBinaryOp.Divide, Ref(n), Ref(d))),
      new IrDeclare(r, Binary(IrBinaryOp.Remainder, Ref(n), Ref(d))),
      new IrDeclare(e, Int32(0)),
      new IrDeclare(sticky, UInt(0)),
      // A quotient of more than 55 bits loses its low bits into the sticky bit ...
      new IrWhile(Binary(IrBinaryOp.GreaterOrEqual, Ref(q), UInt(1UL << 55)), Block(
        Assign(sticky, Binary(IrBinaryOp.Or, Ref(sticky), Binary(IrBinaryOp.And, Ref(q), UInt(1)))),
        Assign(q, Binary(IrBinaryOp.ShiftRightLogical, Ref(q), Int32(1))),
        Assign(e, Binary(IrBinaryOp.Add, Ref(e), Int32(1))))),
      // ... a shorter one takes the next bits of the fraction, one by one, without overflow: 2r >= d is r >= d - r.
      new IrWhile(Binary(IrBinaryOp.Less, Ref(q), UInt(1UL << 54)), Block(
        Assign(q, Binary(IrBinaryOp.Add, Ref(q), Ref(q))),
        Assign(e, Binary(IrBinaryOp.Subtract, Ref(e), Int32(1))),
        new IrIf(Binary(IrBinaryOp.GreaterOrEqual, Ref(r), Binary(IrBinaryOp.Subtract, Ref(d), Ref(r))),
          Block(Assign(q, Binary(IrBinaryOp.Add, Ref(q), UInt(1))), Assign(r, Binary(IrBinaryOp.Subtract, Ref(r), Binary(IrBinaryOp.Subtract, Ref(d), Ref(r))))),
          Block(Assign(r, Binary(IrBinaryOp.Add, Ref(r), Ref(r))))))),
      // The rest of the fraction is the sticky bit; 53 bits, a rounding bit and the sticky bit round once.
      Assign(q, Binary(IrBinaryOp.Or, Ref(q), Binary(IrBinaryOp.Or, Ref(sticky), Choose(Binary(IrBinaryOp.NotEqual, Ref(r), UInt(0)), UInt(1), UInt(0))))),
      // Scaled by powers of two, which is exact: the quotient lies between 2^-64 and 2^64, far from the subnormals and the infinities.
      new IrDeclare(magnitude, Convert(IrType.F64, Ref(q))),
      new IrWhile(Binary(IrBinaryOp.Greater, Ref(e), Int32(0)), Block(
        Assign(magnitude, Binary(IrBinaryOp.Multiply, Ref(magnitude), Float(2))),
        Assign(e, Binary(IrBinaryOp.Subtract, Ref(e), Int32(1))))),
      new IrWhile(Binary(IrBinaryOp.Less, Ref(e), Int32(0)), Block(
        Assign(magnitude, Binary(IrBinaryOp.Multiply, Ref(magnitude), Float(0.5))),
        Assign(e, Binary(IrBinaryOp.Add, Ref(e), Int32(1))))),
      Return(Choose(Binary(IrBinaryOp.NotEqual, Binary(IrBinaryOp.Less, Ref(a), Int(0)), Binary(IrBinaryOp.Less, Ref(b), Int(0))),
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

  private IrFunction MakeModuloInt(string name) {
    var (a, b) = (Local("a", IrType.I64), Local("b", IrType.I64));
    return Function(name, IrType.I64, [a, b], [],
      If(Equal(Ref(b), Int(-1)), Return(Int(0))),
      Return(Intrinsic(IrIntrinsicOp.ModFloor, IrType.I64, Ref(a), Ref(b))));
  }

  private IrFunction MakePowerInt(string name) {
    var (a, b) = (Local("a", IrType.I64), Local("b", IrType.I64));
    var result = Local("result", IrType.I64);
    return Function(name, IrType.I64, [a, b], [result],
      If(Binary(IrBinaryOp.Less, Ref(b), Int(0)), Raise(IrExceptionClass.ArgumentException, "negative exponent of an int whose sign is not known")),
      new IrDeclare(result, Int(1)),
      // Square and multiply; a square is only made where a higher bit of the exponent needs it, so it overflows only where the result does.
      new IrWhile(Binary(IrBinaryOp.Greater, Ref(b), Int(0)), Block(
        If(Binary(IrBinaryOp.NotEqual, Binary(IrBinaryOp.And, Ref(b), Int(1)), Int(0)), Assign(result, Binary(IrBinaryOp.MultiplyChecked, Ref(result), Ref(a)))),
        Assign(b, Binary(IrBinaryOp.ShiftRight, Ref(b), Int32(1))),
        If(Binary(IrBinaryOp.Greater, Ref(b), Int(0)), Assign(a, Binary(IrBinaryOp.MultiplyChecked, Ref(a), Ref(a)))))),
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
    var (a, n) = (Local("a", IrType.I64), Local("n", IrType.I64));
    var result = Local("result", IrType.I64);
    return Function(name, IrType.I64, [a, n], [result],
      If(Binary(IrBinaryOp.Less, Ref(n), Int(0)), Raise(IrExceptionClass.ArgumentException, "negative shift count")),
      If(Equal(Ref(a), Int(0)), Return(Int(0))),
      If(Binary(IrBinaryOp.GreaterOrEqual, Ref(n), Int(64)), Raise(IrExceptionClass.OverflowException, "int too large")),
      new IrDeclare(result, Binary(IrBinaryOp.ShiftLeft, Ref(a), Convert(IrType.I32, Ref(n)))),
      If(Binary(IrBinaryOp.NotEqual, Binary(IrBinaryOp.ShiftRight, Ref(result), Convert(IrType.I32, Ref(n))), Ref(a)), Raise(IrExceptionClass.OverflowException, "int too large")),
      Return(Ref(result)));
  }

  private IrFunction MakeShiftRight(string name) {
    var (a, n) = (Local("a", IrType.I64), Local("n", IrType.I64));
    return Function(name, IrType.I64, [a, n], [],
      If(Binary(IrBinaryOp.Less, Ref(n), Int(0)), Raise(IrExceptionClass.ArgumentException, "negative shift count")),
      If(Binary(IrBinaryOp.GreaterOrEqual, Ref(n), Int(64)), Return(Choose(Binary(IrBinaryOp.Less, Ref(a), Int(0)), Int(-1), Int(0)))),
      Return(Binary(IrBinaryOp.ShiftRight, Ref(a), Convert(IrType.I32, Ref(n)))));
  }

  private IrFunction MakeIntOfFloat(string name) {
    var x = Local("x", IrType.F64);
    var truncated = Local("truncated", IrType.F64);
    return Function(name, IrType.I64, [x], [truncated],
      If(Intrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, Ref(x)), Raise(IrExceptionClass.ArgumentException, "cannot convert float NaN to integer")),
      If(Intrinsic(IrIntrinsicOp.IsInfinity, IrType.Bool, Ref(x)), Raise(IrExceptionClass.OverflowException, "cannot convert float infinity to integer")),
      new IrDeclare(truncated, Intrinsic(IrIntrinsicOp.Truncate, IrType.F64, Ref(x))),
      If(Binary(IrBinaryOp.LogicalOr, Binary(IrBinaryOp.Less, Ref(truncated), Float(-9223372036854775808.0)), Binary(IrBinaryOp.GreaterOrEqual, Ref(truncated), Float(9223372036854775808.0))),
        Raise(IrExceptionClass.OverflowException, "int too large")),
      Return(new IrConvert(IrType.I64, Ref(truncated), IrConversion.Trap)));
  }

  private IrFunction MakeAbsInt(string name) {
    var a = Local("a", IrType.I64);
    return Function(name, IrType.I64, [a], [],
      If(Binary(IrBinaryOp.Less, Ref(a), Int(0)), Return(new IrUnary(IrUnaryOp.NegateChecked, Ref(a)))),
      Return(Ref(a)));
  }

  private IrFunction MakeCompareIntFloat(string name) {
    var (a, b, op) = (Local("a", IrType.I64), Local("b", IrType.F64), Local("op", IrType.I32));
    var (order, truncated, whole) = (Local("order", IrType.I32), Local("truncated", IrType.F64), Local("whole", IrType.I64));
    IrExpr Is(int code) => Equal(Ref(op), Int32(code));
    IrExpr Sign(IrBinaryOp op, IrLocal left, IrLocal right) => Choose(Binary(op, Ref(left), Ref(right)), Int32(1), Int32(0));
    // Outside the range of i64 the float is beyond every int; inside, its integral part is an i64
    // exactly, which decides unless it equals the int, and then the fraction decides.
    var inside = Block(
      new IrDeclare(truncated, Intrinsic(IrIntrinsicOp.Truncate, IrType.F64, Ref(b))),
      new IrDeclare(whole, new IrConvert(IrType.I64, Ref(truncated), IrConversion.Trap)),
      Assign(order, Binary(IrBinaryOp.Subtract, Sign(IrBinaryOp.Greater, a, whole), Sign(IrBinaryOp.Less, a, whole))),
      If(Equal(Ref(order), Int32(0)), Assign(order, Binary(IrBinaryOp.Subtract, Sign(IrBinaryOp.Less, b, truncated), Sign(IrBinaryOp.Greater, b, truncated)))));
    var below = new IrIf(Binary(IrBinaryOp.Less, Ref(b), Float(-9223372036854775808.0)), Block(Assign(order, Int32(1))), inside);
    var above = new IrIf(Binary(IrBinaryOp.GreaterOrEqual, Ref(b), Float(9223372036854775808.0)), Block(Assign(order, Int32(-1))), Block(below));
    var result = Choose(Is(4), Equal(Ref(order), Int32(0)), Binary(IrBinaryOp.NotEqual, Ref(order), Int32(0)));
    result = Choose(Is(3), Binary(IrBinaryOp.GreaterOrEqual, Ref(order), Int32(0)), result);
    result = Choose(Is(2), Binary(IrBinaryOp.Greater, Ref(order), Int32(0)), result);
    result = Choose(Is(1), Binary(IrBinaryOp.LessOrEqual, Ref(order), Int32(0)), result);
    result = Choose(Is(0), Binary(IrBinaryOp.Less, Ref(order), Int32(0)), result);
    return Function(name, IrType.Bool, [a, b, op], [order, truncated, whole],
      If(Intrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, Ref(b)), Return(Is(5))),
      new IrDeclare(order, Int32(0)),
      above,
      Return(result));
  }

  private IrFunction MakeCompareFloatInt(string name) {
    var (a, b, op) = (Local("a", IrType.F64), Local("b", IrType.I64), Local("op", IrType.I32));
    var compare = this.Call("PyCompareIntFloat", IrType.Bool, [], this.MakeCompareIntFloat).Function;
    // The operands are evaluated in the caller's order; the comparison is the mirrored one of the swapped operands.
    return Function(name, IrType.Bool, [a, b, op], [],
      Return(new IrCall(compare, IrType.Bool, [Ref(b), Ref(a), Choose(Equal(Ref(op), Int32(0)), Int32(2),
        Choose(Equal(Ref(op), Int32(1)), Int32(3), Choose(Equal(Ref(op), Int32(2)), Int32(0), Choose(Equal(Ref(op), Int32(3)), Int32(1), Ref(op)))))])));
  }

  /// <summary>The names every helper may take, so that the lowering keeps them free for its own functions.</summary>
  public static IEnumerable<string> Keys => ["PyModuloInt", "PyTrueDivideInt", "PyTrueDivideFloat", "PyFloorDivideFloat", "PyModuloFloat", "PyPowerInt", "PyPowerFloat",
    "PyShiftLeft", "PyShiftRight", "PyIntOfFloat", "PyAbsInt", "PyCompareIntFloat", "PyCompareFloatInt"];

}
