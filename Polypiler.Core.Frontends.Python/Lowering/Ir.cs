using System.Collections.Generic;
using System.Numerics;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>Short forms of the IR nodes the lowering builds.</summary>
internal static class Ir {

  /// <summary>An <c>i64</c> constant.</summary>
  public static IrConst Int(long value) => new(IrType.I64, value);

  /// <summary>A constant of Python's <c>int</c>: an arbitrary-precision integer.</summary>
  public static IrBigConst Big(BigInteger value) => new(value);

  /// <summary>An <c>i32</c> constant.</summary>
  public static IrConst Int32(int value) => new(IrType.I32, value);

  /// <summary>A <c>u64</c> constant.</summary>
  public static IrConst UInt(ulong value) => new(IrType.U64, unchecked((long)value));

  /// <summary>An <c>f64</c> constant.</summary>
  public static IrConst Float(double value) => new(IrType.F64, IrOperators.Bits(IrType.F64, value));

  /// <summary>A truth value.</summary>
  public static IrConst Bool(bool value) => new(IrType.Bool, value ? 1 : 0);

  /// <summary>The value of a variable.</summary>
  public static IrLocalRef Ref(IrLocal local) => new(local);

  /// <summary>A binary operation.</summary>
  public static IrBinary Binary(IrBinaryOp op, IrExpr left, IrExpr right) => new(op, left, right);

  /// <summary>A comparison for equality.</summary>
  public static IrBinary Equal(IrExpr left, IrExpr right) => new(IrBinaryOp.Equal, left, right);

  /// <summary>The negation of a truth value.</summary>
  public static IrUnary Not(IrExpr operand) => new(IrUnaryOp.LogicalNot, operand);

  /// <summary>A conversion that wraps around where it does not fit, the only kind the helpers use.</summary>
  public static IrConvert Convert(IrType type, IrExpr operand) => new(type, operand);

  /// <summary>An intrinsic operation.</summary>
  public static IrIntrinsic Intrinsic(IrIntrinsicOp op, IrType type, params IReadOnlyList<IrExpr> arguments) => new(op, type, arguments);

  /// <summary>A choice of two values.</summary>
  public static IrConditional Choose(IrExpr condition, IrExpr whenTrue, IrExpr whenFalse) => new(condition, whenTrue, whenFalse);

  /// <summary>A block of statements.</summary>
  public static IrBlock Block(params IReadOnlyList<IrStmt> statements) => new(statements);

  /// <summary>A conditional statement without an else branch.</summary>
  public static IrIf If(IrExpr condition, params IReadOnlyList<IrStmt> then) => new(condition, new(then), null);

  /// <summary>An assignment.</summary>
  public static IrAssign Assign(IrLocal local, IrExpr value) => new(local, value);

  /// <summary>A return of a value.</summary>
  public static IrReturn Return(IrExpr value) => new(value);

  /// <summary>
  /// The raise of a Python exception: the IR's exception class that stands for it, made with
  /// python's message (docs/reference/exceptions.md, "Python").
  /// </summary>
  public static IrThrow Raise(IrClassType type, string message)
    => new(new IrNew(IrType.Object(type), IrExceptionClass.Constructor(type, 1), [new IrStrConst(message)]));

}
