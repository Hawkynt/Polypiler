using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Polypiler.Core.IR;

namespace Polypiler.Tests.BigInt;

/// <summary>
/// One module that uses every operation and conversion of the arbitrary-precision integer, the
/// operands every backend runs it on, and the interpreter's outcome of a call as the oracle: the
/// interpreter's arithmetic is .NET's <see cref="BigInteger"/>, checked against CPython in
/// <see cref="IrBigIntTests"/>.
/// </summary>
internal static class BigIntCases {

  /// <summary>The operands: zero and the signs, the boundaries of 8, 32, 63 and 64 bits on both sides, and numbers far beyond them.</summary>
  public static readonly BigInteger[] Values = [
    0, 1, -1, 255, -256, 4294967295, -2147483649, long.MaxValue, (BigInteger)long.MaxValue + 1, long.MinValue, (BigInteger)long.MinValue - 1,
    ulong.MaxValue, (BigInteger)ulong.MaxValue + 1, BigInteger.Pow(3, 50), -BigInteger.Pow(3, 50), BigInteger.Pow(2, 1100) + 1
  ];

  /// <summary>The shift counts: zero, small, at and beyond the widths, and negative ones, which shift the other way.</summary>
  public static readonly int[] Counts = [0, 1, 7, 63, 64, 65, 200, -1, -64];

  /// <summary>The floating-point operands of the conversion to the integer: fractions, the 53- and 64-bit limits, a huge one and both zeros.</summary>
  public static readonly double[] Floats = [0.0, -0.0, 2.9, -2.9, 9007199254740993.0, 18446744073709551616.0, -1e300, 1.5e-300];

  private static readonly IrLocal A = new("a", IrType.BigInt), B = new("b", IrType.BigInt), N = new("n", IrType.I32);
  private static readonly IrLocal X = new("x", IrType.F64), I = new("i", IrType.I32), U = new("u", IrType.U64);

  private static IrLocalRef R(IrLocal local) => new(local);

  /// <summary>The binary operators, each a function <c>(a, b)</c>.</summary>
  public static readonly IrBinaryOp[] Binary = [
    IrBinaryOp.Add, IrBinaryOp.Subtract, IrBinaryOp.Multiply, IrBinaryOp.Divide, IrBinaryOp.Remainder, IrBinaryOp.And, IrBinaryOp.Or, IrBinaryOp.Xor,
    IrBinaryOp.Equal, IrBinaryOp.NotEqual, IrBinaryOp.Less, IrBinaryOp.LessOrEqual, IrBinaryOp.Greater, IrBinaryOp.GreaterOrEqual
  ];

  /// <summary>The conversions to a fixed width, each a function <c>(a)</c>, by name.</summary>
  public static readonly (string Name, IrType Type, IrConversion Contract)[] Narrowing = [
    ("WrapI8", IrType.I8, IrConversion.Wrap), ("WrapU8", IrType.U8, IrConversion.Wrap), ("WrapI16", IrType.I16, IrConversion.Wrap), ("WrapU16", IrType.U16, IrConversion.Wrap),
    ("WrapI32", IrType.I32, IrConversion.Wrap), ("WrapU32", IrType.U32, IrConversion.Wrap), ("WrapI64", IrType.I64, IrConversion.Wrap), ("WrapU64", IrType.U64, IrConversion.Wrap),
    ("TrapI32", IrType.I32, IrConversion.Trap), ("TrapI64", IrType.I64, IrConversion.Trap), ("TrapU64", IrType.U64, IrConversion.Trap)
  ];

  /// <summary>The module.</summary>
  public static IrModule Module() {
    var functions = new List<IrFunction>();
    IrFunction F(string name, IrType result, IReadOnlyList<IrLocal> parameters, IrExpr value) => new(name, true, result, parameters, [], new IrBlock([new IrReturn(value)]));
    functions.AddRange(Binary.Select(op => F(op.ToString(), op >= IrBinaryOp.Equal ? IrType.Bool : IrType.BigInt, [A, B], new IrBinary(op, R(A), R(B)))));
    functions.Add(F("ShiftLeft", IrType.BigInt, [A, N], new IrBinary(IrBinaryOp.ShiftLeft, R(A), R(N))));
    functions.Add(F("ShiftRight", IrType.BigInt, [A, N], new IrBinary(IrBinaryOp.ShiftRight, R(A), R(N))));
    functions.Add(F("Negate", IrType.BigInt, [A], new IrUnary(IrUnaryOp.Negate, R(A))));
    functions.Add(F("Complement", IrType.BigInt, [A], new IrUnary(IrUnaryOp.BitNot, R(A))));
    functions.AddRange(Narrowing.Select(narrow => F(narrow.Name, narrow.Type, [A], new IrConvert(narrow.Type, R(A), narrow.Contract))));
    functions.Add(F("ToDouble", IrType.F64, [A], new IrConvert(IrType.F64, R(A))));
    functions.Add(F("FromDouble", IrType.BigInt, [X], new IrConvert(IrType.BigInt, R(X), IrConversion.Trap)));
    functions.Add(F("FromInt", IrType.BigInt, [I], new IrConvert(IrType.BigInt, R(I))));
    functions.Add(F("FromULong", IrType.BigInt, [U], new IrConvert(IrType.BigInt, R(U))));
    functions.Add(F("Constants", IrType.BigInt, [], new IrBinary(IrBinaryOp.Subtract, new IrBigConst(BigInteger.Pow(10, 30) + 7), new IrBigConst(-5))));
    // A local of the type, assigned in a loop: 3^k by repeated multiplication.
    var power = new IrLocal("power", IrType.BigInt);
    var k = new IrLocal("k", IrType.I32);
    functions.Add(new IrFunction("PowerOfThree", true, IrType.BigInt, [k], [power], new IrBlock([
      new IrDeclare(power, new IrBigConst(1)),
      new IrWhile(new IrBinary(IrBinaryOp.Greater, R(k), new IrConst(IrType.I32, 0)), new IrBlock([
        new IrAssign(power, new IrBinary(IrBinaryOp.Multiply, R(power), new IrBigConst(3))),
        new IrAssign(k, new IrBinary(IrBinaryOp.Subtract, R(k), new IrConst(IrType.I32, 1)))])),
      new IrReturn(R(power))])));
    return new IrModule("BigInts", functions);
  }

  /// <summary>Every call: a binary operator on every pair of operands, a shift on every operand and count, a conversion on every operand.</summary>
  public static IEnumerable<(string Function, object[] Arguments)> Calls() {
    foreach (var op in Binary)
      foreach (var a in Values)
        foreach (var b in Values)
          yield return (op.ToString(), [a, b]);

    foreach (var a in Values) {
      foreach (var n in Counts) {
        yield return ("ShiftLeft", [a, n]);
        yield return ("ShiftRight", [a, n]);
      }

      foreach (var name in new[] { "Negate", "Complement", "ToDouble" }.Concat(Narrowing.Select(narrow => narrow.Name)))
        yield return (name, [a]);
    }

    foreach (var x in Floats.Concat([double.NaN, double.PositiveInfinity]))
      yield return ("FromDouble", [x]);

    foreach (var i in new[] { 0, -1, int.MinValue, int.MaxValue })
      yield return ("FromInt", [i]);

    yield return ("FromULong", [ulong.MaxValue]);
    yield return ("Constants", []);
    yield return ("PowerOfThree", [0]);
    yield return ("PowerOfThree", [100]);
  }

  /// <summary>An outcome as the tests compare it: the value in the invariant culture, or the trap by its class of the IR.</summary>
  public static string Show(object? value) => value switch {
    null => "void",
    double number => "f " + number.ToString("R", CultureInfo.InvariantCulture),
    bool truth => truth ? "true" : "false",
    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
    _ => value.ToString()!
  };

  /// <summary>What the interpreter does for a call.</summary>
  public static string Interpreted(IrModule module, string function, object[] arguments) {
    try {
      return Show(IrInterpreter.Call(module, function, arguments));
    } catch (IrException exception) {
      return "raise " + exception.Class.Name;
    }
  }

}
