using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>A named, typed variable of a function: a parameter or a declared local.</summary>
/// <param name="Name">The identifier as the source wrote it.</param>
/// <param name="Type">The type of every value the variable holds.</param>
public sealed record IrLocal(string Name, IrType Type);

/// <summary>An expression; it evaluates its operands left to right and yields a value of <see cref="Type"/>.</summary>
/// <param name="Type">The type of the value, which fixes width, signedness and wrap-around.</param>
public abstract record IrExpr(IrType Type);

/// <summary>A constant. Integers carry their numeric value, truth values 0 or 1.</summary>
/// <param name="Type">The type of the constant.</param>
/// <param name="Value">The value, within the range of <paramref name="Type"/>.</param>
public sealed record IrConst(IrType Type, long Value) : IrExpr(Type);

/// <summary>The current value of a parameter or local.</summary>
/// <param name="Local">The variable read.</param>
public sealed record IrLocalRef(IrLocal Local) : IrExpr(Local.Type);

/// <summary>The operators with one operand.</summary>
public enum IrUnaryOp {

  /// <summary>Two's-complement negation of an <c>i32</c>; the smallest value negates to itself.</summary>
  Negate,

  /// <summary>Bitwise complement of a 32-bit value.</summary>
  BitNot,

  /// <summary>Negation of a truth value.</summary>
  LogicalNot

}

/// <summary>An operator applied to one operand; the result has the operand's type.</summary>
/// <param name="Op">The operator.</param>
/// <param name="Operand">The operand.</param>
public sealed record IrUnary(IrUnaryOp Op, IrExpr Operand) : IrExpr(Operand.Type);

/// <summary>The operators with two operands.</summary>
public enum IrBinaryOp {

  /// <summary>Addition modulo 2^32.</summary>
  Add,

  /// <summary>Subtraction modulo 2^32.</summary>
  Subtract,

  /// <summary>Multiplication modulo 2^32.</summary>
  Multiply,

  /// <summary>
  /// Division truncating towards zero. Traps on a zero divisor, and on the one <c>i32</c> quotient
  /// that does not fit (smallest value divided by -1).
  /// </summary>
  Divide,

  /// <summary>Remainder with the sign of the dividend; traps exactly where <see cref="Divide"/> does.</summary>
  Remainder,

  /// <summary>Bitwise and.</summary>
  And,

  /// <summary>Bitwise or.</summary>
  Or,

  /// <summary>Bitwise exclusive or.</summary>
  Xor,

  /// <summary>Shift left by the low 5 bits of the <c>i32</c> count.</summary>
  ShiftLeft,

  /// <summary>Shift right by the low 5 bits of the count: arithmetic on <c>i32</c>, logical on <c>u32</c>.</summary>
  ShiftRight,

  /// <summary>Shift right by the low 5 bits of the count, filling with zeros for either type.</summary>
  ShiftRightLogical,

  /// <summary>Equality of two 32-bit values or two truth values.</summary>
  Equal,

  /// <summary>Inequality of two 32-bit values or two truth values.</summary>
  NotEqual,

  /// <summary>Less than, in the signedness of the operands.</summary>
  Less,

  /// <summary>Less than or equal, in the signedness of the operands.</summary>
  LessOrEqual,

  /// <summary>Greater than, in the signedness of the operands.</summary>
  Greater,

  /// <summary>Greater than or equal, in the signedness of the operands.</summary>
  GreaterOrEqual,

  /// <summary>Conjunction that evaluates the right operand only when the left one is true.</summary>
  LogicalAnd,

  /// <summary>Disjunction that evaluates the right operand only when the left one is false.</summary>
  LogicalOr

}

/// <summary>
/// An operator applied to two operands. Arithmetic, bitwise and shift operators yield the type of the
/// left operand; comparisons and the logical operators yield a truth value.
/// </summary>
/// <param name="Op">The operator.</param>
/// <param name="Left">The operand evaluated first.</param>
/// <param name="Right">The operand evaluated second.</param>
public sealed record IrBinary(IrBinaryOp Op, IrExpr Left, IrExpr Right)
  : IrExpr(Op >= IrBinaryOp.Equal ? IrType.Bool : Left.Type);

/// <summary>A conversion between integer types: the value modulo 2^width of the target type.</summary>
/// <param name="Type">The target type.</param>
/// <param name="Operand">The value converted.</param>
public sealed record IrConvert(IrType Type, IrExpr Operand) : IrExpr(Type);

/// <summary>A choice that evaluates the condition and then exactly one of the two branches.</summary>
/// <param name="Condition">The truth value deciding the branch.</param>
/// <param name="WhenTrue">The value when the condition holds.</param>
/// <param name="WhenFalse">The value when it does not.</param>
public sealed record IrConditional(IrExpr Condition, IrExpr WhenTrue, IrExpr WhenFalse) : IrExpr(WhenTrue.Type);

/// <summary>An element of an array; traps when the index is outside the array.</summary>
/// <param name="Array">The array read.</param>
/// <param name="Index">The zero-based index.</param>
public sealed record IrArrayLoad(IrExpr Array, IrExpr Index) : IrExpr(Array.Type.Element ?? IrType.Void);

/// <summary>The number of elements of an array, as an <c>i32</c>.</summary>
/// <param name="Array">The array measured.</param>
public sealed record IrArrayLength(IrExpr Array) : IrExpr(IrType.I32);

/// <summary>A new array whose elements are all zero; traps on a negative length.</summary>
/// <param name="Type">The array type.</param>
/// <param name="Length">The number of elements.</param>
public sealed record IrNewArray(IrType Type, IrExpr Length) : IrExpr(Type);

/// <summary>A call of another function of the same module; arguments are evaluated left to right.</summary>
/// <param name="Function">The name of the function called.</param>
/// <param name="Type">The return type of that function.</param>
/// <param name="Arguments">One argument per parameter.</param>
public sealed record IrCall(string Function, IrType Type, IReadOnlyList<IrExpr> Arguments) : IrExpr(Type);

/// <summary>A statement.</summary>
public abstract record IrStmt;

/// <summary>A sequence of statements with its own scope for declared locals.</summary>
/// <param name="Statements">The statements, in execution order.</param>
public sealed record IrBlock(IReadOnlyList<IrStmt> Statements) : IrStmt;

/// <summary>The declaration of a local, visible until the enclosing block ends.</summary>
/// <param name="Local">The local declared.</param>
/// <param name="Initializer">Its first value, or <see langword="null"/> when it is assigned later.</param>
public sealed record IrDeclare(IrLocal Local, IrExpr? Initializer) : IrStmt;

/// <summary>An assignment to a parameter or local.</summary>
/// <param name="Local">The variable written.</param>
/// <param name="Value">The value stored.</param>
public sealed record IrAssign(IrLocal Local, IrExpr Value) : IrStmt;

/// <summary>
/// A write to an array element. Array, index and value are evaluated in that order, then the index
/// is checked; the write traps when it is outside the array.
/// </summary>
/// <param name="Array">The array written.</param>
/// <param name="Index">The zero-based index.</param>
/// <param name="Value">The value stored, of the element type.</param>
public sealed record IrStore(IrExpr Array, IrExpr Index, IrExpr Value) : IrStmt;

/// <summary>An expression evaluated for its effect; the value is dropped.</summary>
/// <param name="Expression">The expression.</param>
public sealed record IrExprStmt(IrExpr Expression) : IrStmt;

/// <summary>A conditional statement.</summary>
/// <param name="Condition">The truth value deciding the branch.</param>
/// <param name="Then">Executed when the condition holds.</param>
/// <param name="Else">Executed when it does not, or <see langword="null"/>.</param>
public sealed record IrIf(IrExpr Condition, IrBlock Then, IrBlock? Else) : IrStmt;

/// <summary>A loop that tests its condition before every pass.</summary>
/// <param name="Condition">The truth value that keeps the loop running.</param>
/// <param name="Body">The statements of one pass.</param>
public sealed record IrWhile(IrExpr Condition, IrBlock Body) : IrStmt;

/// <summary>Leaves the function.</summary>
/// <param name="Value">The value returned, or <see langword="null"/> in a function returning nothing.</param>
public sealed record IrReturn(IrExpr? Value) : IrStmt;

/// <summary>A function.</summary>
/// <param name="Name">The name, unique in its module.</param>
/// <param name="IsPublic">Whether the function is visible outside the module.</param>
/// <param name="ReturnType">The type returned, or <see cref="IrType.Void"/>.</param>
/// <param name="Parameters">The parameters, in call order.</param>
/// <param name="Locals">Every local the body declares.</param>
/// <param name="Body">The statements of the function.</param>
public sealed record IrFunction(
  string Name,
  bool IsPublic,
  IrType ReturnType,
  IReadOnlyList<IrLocal> Parameters,
  IReadOnlyList<IrLocal> Locals,
  IrBlock Body);

/// <summary>A module: the functions of one source class.</summary>
/// <param name="Name">The name of the module.</param>
/// <param name="Functions">The functions, in source order.</param>
public sealed record IrModule(string Name, IReadOnlyList<IrFunction> Functions);
