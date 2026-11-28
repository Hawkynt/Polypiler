using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Dynamic;

/// <summary>The operations of a dynamic language a semantics table states (ADR 0300 decision 4).</summary>
public enum DynamicOperation {

  /// <summary><c>+</c> of two values.</summary>
  Add,

  /// <summary><c>-</c> of two values.</summary>
  Subtract,

  /// <summary><c>*</c> of two values.</summary>
  Multiply,

  /// <summary><c>/</c>: the division that gives a floating-point number.</summary>
  Divide,

  /// <summary>The integer division, Visual Basic's <c>\</c>.</summary>
  IntegerDivide,

  /// <summary>The remainder, Visual Basic's <c>Mod</c>.</summary>
  Modulo,

  /// <summary>The power, Visual Basic's <c>^</c>.</summary>
  Power,

  /// <summary>The concatenation of the texts of two values, Visual Basic's <c>&amp;</c>.</summary>
  Concatenate,

  /// <summary>The unary <c>-</c>.</summary>
  Negate,

  /// <summary>The logical or bitwise <c>Not</c>.</summary>
  Not,

  /// <summary>The logical or bitwise <c>And</c>.</summary>
  And,

  /// <summary>The logical or bitwise <c>Or</c>.</summary>
  Or,

  /// <summary>The logical or bitwise <c>Xor</c>.</summary>
  Xor,

  /// <summary>Visual Basic's <c>Eqv</c>.</summary>
  Eqv,

  /// <summary>Visual Basic's <c>Imp</c>.</summary>
  Imp,

  /// <summary><c>=</c>.</summary>
  Equal,

  /// <summary><c>&lt;&gt;</c>.</summary>
  NotEqual,

  /// <summary><c>&lt;</c>.</summary>
  Less,

  /// <summary><c>&lt;=</c>.</summary>
  LessOrEqual,

  /// <summary><c>&gt;</c>.</summary>
  Greater,

  /// <summary><c>&gt;=</c>.</summary>
  GreaterOrEqual,

  /// <summary>A value as a condition: an IR <c>bool</c>.</summary>
  Truthiness,

  /// <summary>A value converted to a number, an IR <c>f64</c>.</summary>
  ToNumber,

  /// <summary>A value converted to a string, an IR <c>str</c>.</summary>
  ToString,

  /// <summary>An element of a value by an index.</summary>
  Index,

  /// <summary>A member of a value by its name.</summary>
  MemberGet,

  /// <summary>A member of a value written.</summary>
  MemberSet,

  /// <summary>A call of a value.</summary>
  Call

}

/// <summary>How an operation of a pair of operand elements may fail: with a trap of the IR, with an error of the language, both or neither.</summary>
/// <param name="Trap">The trap of the IR it may raise, or <see langword="null"/>.</param>
/// <param name="LanguageError">The error number of the language it may raise, such as Visual Basic's 13, or <see langword="null"/>.</param>
public readonly record struct DynamicTrap(IrTrap? Trap, int? LanguageError) {

  /// <summary>An operation that never fails.</summary>
  public static DynamicTrap None => default;

  /// <summary>An operation that may raise an error of the language.</summary>
  /// <param name="number">The error number.</param>
  /// <returns>The trap.</returns>
  public static DynamicTrap Error(int number) => new(null, number);

  /// <summary>Whether the operation may fail at all.</summary>
  public bool MayFail => this.Trap is not null || this.LanguageError is not null;

}

/// <summary>An operation of a dynamic language the frontend lowered as a call of its helper, as the table recognises it.</summary>
/// <param name="Operation">The operation.</param>
/// <param name="Left">The first operand; the only one of a unary operation.</param>
/// <param name="Right">The second operand, or <see langword="null"/> for a unary operation.</param>
public sealed record DynamicCall(DynamicOperation Operation, IrExpr Left, IrExpr? Right);

/// <summary>
/// The rules of one dynamic language, as data the framework reads (ADR 0300 decision 4): for every
/// operation and pair of operand elements, the element of the result, how it may fail and the IR it
/// expands to. A frontend lowers a value it cannot type as a <c>variant</c> and an operation on one as a
/// call of its helper; <see cref="Classify"/> recognises such a call, and the framework, which knows
/// the operands' elements, asks <see cref="Result"/> and <see cref="Expand"/>. Nothing of a language
/// lives in the framework itself.
/// </summary>
public interface IDynamicSemantics {

  /// <summary>The language, as <see cref="IrModule.DynamicSemantics"/> names it, such as <c>visualbasic</c>.</summary>
  string Language { get; }

  /// <summary>Whether two exact scalars of the language can be told apart.</summary>
  IJoinPolicy Joins { get; }

  /// <summary>The element of the result of an operation on operands of two elements.</summary>
  /// <param name="operation">The operation.</param>
  /// <param name="left">The element of the first operand.</param>
  /// <param name="right">The element of the second operand; <see cref="DynamicType.Nothing"/> for a unary operation.</param>
  /// <returns>The element; <see cref="DynamicType.Variant"/> where the table states nothing narrower.</returns>
  DynamicType Result(DynamicOperation operation, DynamicType left, DynamicType right);

  /// <summary>How an operation on operands of two elements may fail.</summary>
  /// <param name="operation">The operation.</param>
  /// <param name="left">The element of the first operand.</param>
  /// <param name="right">The element of the second operand; <see cref="DynamicType.Nothing"/> for a unary operation.</param>
  /// <returns>The trap.</returns>
  DynamicTrap Trap(DynamicOperation operation, DynamicType left, DynamicType right);

  /// <summary>
  /// The IR an operation on operands of two elements expands to: typed operations of the IR type of
  /// the <see cref="Result"/>, which must be exact, with every trap of <see cref="Trap"/> kept; or
  /// <see langword="null"/> where the pair has no typed expansion and the call of the language's
  /// helper stays.
  /// </summary>
  /// <param name="operation">The operation.</param>
  /// <param name="left">The first operand: of the IR type of its element where that is exact, else a <c>variant</c>.</param>
  /// <param name="leftType">The element of the first operand.</param>
  /// <param name="right">The second operand, the same way; <see langword="null"/> for a unary operation.</param>
  /// <param name="rightType">The element of the second operand; <see cref="DynamicType.Nothing"/> for a unary operation.</param>
  /// <returns>The expansion, or <see langword="null"/>.</returns>
  IrExpr? Expand(DynamicOperation operation, IrExpr left, DynamicType leftType, IrExpr? right, DynamicType rightType);

  /// <summary>The operation an expression of the frontend's lowering is, or <see langword="null"/> for any other expression.</summary>
  /// <param name="expression">The expression, such as a call of one of the language's helpers.</param>
  /// <returns>The operation with its operands, or <see langword="null"/>.</returns>
  DynamicCall? Classify(IrExpr expression);

}
