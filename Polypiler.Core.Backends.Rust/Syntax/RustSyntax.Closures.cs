using System.Collections.Generic;

namespace Polypiler.Core.Backends.Rust.Syntax;

// The nodes of function values: a closure, and a function declared inside a block.

/// <summary>
/// <c>move |a: T, b: U| body</c>: a closure, its parameters typed, its body an expression or a block
/// (<see cref="RsBlockExpression"/>).
/// </summary>
/// <param name="Move">Whether the closure takes what it captures, <c>move</c>.</param>
/// <param name="Parameters">The parameters, each with its type.</param>
/// <param name="Body">The body.</param>
public sealed record RsClosure(bool Move, IReadOnlyList<RsParameter> Parameters, RsExpression Body) : RsExpression;

/// <summary><c>(a, b)</c>: a tuple, such as the key and the value of an entry of a map.</summary>
/// <param name="Elements">The elements, two or more.</param>
public sealed record RsTuple(IReadOnlyList<RsExpression> Elements) : RsExpression;

/// <summary><c>value?</c>: the value of an <c>Ok</c>, or the <c>Err</c> returned from the function.</summary>
/// <param name="Operand">The <c>Result</c>.</param>
public sealed record RsTry(RsExpression Operand) : RsExpression;

/// <summary><c>value.await</c>: awaits a future inside an <c>async fn</c>.</summary>
/// <param name="Operand">The future.</param>
public sealed record RsAwait(RsExpression Operand) : RsExpression;

/// <summary><c>let Err(error) = outcome</c>: the condition of an <c>if let</c>, which holds where the value matches the pattern.</summary>
/// <param name="Pattern">The pattern as it is written, of names the backend chose.</param>
/// <param name="Value">The value matched.</param>
public sealed record RsLetCondition(string Pattern, RsExpression Value) : RsExpression;

/// <summary>An item declared among the statements of a block, such as a function that captures nothing: <c>fn f(m: i32) -&gt; i32 { … }</c>.</summary>
/// <param name="Item">The item.</param>
public sealed record RsItemStatement(RsItem Item) : RsStatement;
