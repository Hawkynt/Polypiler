namespace Polypiler.Core.IR;

/// <summary>
/// No value, of an optional type <c>T?</c>.
/// <para>
/// The IR states the meaning of optional values once, here, for every node and every implementation:
/// an optional value holds a value of its underlying type or none, and nothing else happens to it on
/// its own. Lifting is no part of the type - C#'s lifted <c>null + 1</c> and JavaScript's coerced
/// <c>null + 1</c> differ - so a frontend writes the lifted operation out with <see cref="IrIsNone"/>,
/// <see cref="IrUnwrap"/>, <see cref="IrSome"/> and <see cref="IrNone"/>, and no backend has to know
/// either language's rule.
/// </para>
/// </summary>
/// <param name="Type">The optional type it is a value of.</param>
public sealed record IrNone(IrType Type) : IrExpr(Type);

/// <summary>A value as an optional value that holds it.</summary>
/// <param name="Value">The value, of a type an optional type holds.</param>
public sealed record IrSome(IrExpr Value) : IrExpr(IrType.Optional(Value.Type));

/// <summary>Whether an optional value holds none.</summary>
/// <param name="Value">The optional value, evaluated once.</param>
public sealed record IrIsNone(IrExpr Value) : IrExpr(IrType.Bool);

/// <summary>
/// The value an optional value holds. It traps with <see cref="IrTrap.MissingValue"/> when it holds
/// none, as C#'s <c>Nullable&lt;T&gt;.Value</c> throws.
/// </summary>
/// <param name="Value">The optional value, evaluated once.</param>
public sealed record IrUnwrap(IrExpr Value) : IrExpr(Value.Type.Underlying ?? IrType.Void) {

  /// <summary>
  /// Whether the frontend has tested the value first, so that it holds one wherever this node is
  /// evaluated - an operand of a lifted operation. A backend may then read the value without the
  /// test; the meaning is the same, because the trap cannot happen.
  /// </summary>
  public bool IsTested { get; init; }

}

/// <summary>
/// The value an optional value holds, or the fallback when it holds none: C#'s and JavaScript's
/// <c>??</c>. The optional value is evaluated once, first; the fallback only when it holds none.
/// </summary>
/// <param name="Value">The optional value.</param>
/// <param name="Fallback">
/// The value in its place: of the underlying type, which makes the node a value of that type, or of
/// the optional type itself, which keeps it optional.
/// </param>
public sealed record IrCoalesce(IrExpr Value, IrExpr Fallback) : IrExpr(Fallback.Type);

/// <summary>Whether a reference - to an object or to an array - is <c>null</c>.</summary>
/// <param name="Value">The reference, evaluated once.</param>
public sealed record IrIsNull(IrExpr Value) : IrExpr(IrType.Bool);

/// <summary>
/// An assignment as an expression: the value is evaluated, assigned to a local in scope and is the
/// value of the node. It is how a frontend evaluates an operand once that it reads twice - the
/// receiver of <c>?.</c>, an operand of a lifted operator - and how C#'s <c>x ??= v</c> yields its value.
/// </summary>
/// <param name="Local">The local assigned; never the receiver of a method.</param>
/// <param name="Value">The value, of the local's type.</param>
public sealed record IrBind(IrLocal Local, IrExpr Value) : IrExpr(Local.Type);

/// <summary>How the JavaScript absent value of an exported function's result is spelled.</summary>
public enum IrAbsent {

  /// <summary><c>null</c>: what C# has, and what a JavaScript source that returns <c>null</c> spells.</summary>
  Null,

  /// <summary><c>undefined</c>: what a JavaScript function that ends without a value, or <c>return;</c>, gives.</summary>
  Undefined

}
