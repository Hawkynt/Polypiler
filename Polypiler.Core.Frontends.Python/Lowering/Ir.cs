using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>Short forms of the IR nodes the lowering builds.</summary>
internal static class Ir {

  /// <summary>An <c>i64</c> constant.</summary>
  public static IrConst Int(long value) => new(IrType.I64, value);

  /// <summary>A constant of Python's <c>int</c>: an arbitrary-precision integer.</summary>
  public static IrBigConst Big(BigInteger value) => new(value);

  /// <summary>An integer of a fixed width as Python's <c>int</c>, exactly; a constant is converted here.</summary>
  public static IrExpr Exact(IrExpr value)
    => value is IrConst { Type: { IsInteger: true, IsEnum: false } } constant ? Big(IrBigInts.Of(constant.Type, constant.Value))
      : Convert(IrType.BigInt, value.Type.IsEnum ? Convert(IrType.I64, value) : value);

  /// <summary>
  /// Python's <c>int</c> as the <c>i64</c> a held place keeps - a field, a tuple, an optional, a table, an
  /// index, a count: exact, or <c>OverflowError</c> beyond 64 bits; a constant that fits is converted here.
  /// </summary>
  public static IrExpr Word(IrExpr value) => value switch {
    IrBigConst constant when constant.Value >= long.MinValue && constant.Value <= long.MaxValue => Int((long)constant.Value),
    IrConvert { Type.IsBigInt: true, Operand.Type: var inner } converted when inner == IrType.I64 => converted.Operand,
    { Type.IsBigInt: true } => new IrConvert(IrType.I64, value, IrConversion.Trap),
    _ => value
  };

  /// <summary>A value as one of the type the place it goes to has, where the two differ only in how an <c>int</c> is held.</summary>
  public static IrExpr Fit(IrExpr value, IrType type)
    => type == IrType.BigInt && value.Type.IsInteger ? Exact(value)
      : value.Type.IsBigInt && type.IsInteger ? (type == IrType.I64 ? Word(value) : new IrConvert(type, Word(value), IrConversion.Trap))
      : value;

  /// <summary>A struct - a tuple - made of values, each as its field holds it.</summary>
  public static IrStructNew MakeStruct(IrType type, IEnumerable<IrExpr> values)
    => new(type, [.. type.StructType!.Fields.Zip(values).Select(pair => Fit(pair.Second, pair.First.Type))]);

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
