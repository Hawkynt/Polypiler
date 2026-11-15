namespace Polypiler.Core.Backends.JavaScript.Syntax;

/// <summary><c>yield value</c>, or <c>yield* iterable</c> with <paramref name="Delegate"/> (ECMAScript 2015).</summary>
/// <param name="Argument">The value or the iterable, or <see langword="null"/> for <c>yield</c> alone.</param>
/// <param name="Delegate">Whether every value of the iterable is produced, <c>yield*</c>.</param>
public sealed record JsYield(JsExpression? Argument, bool Delegate) : JsExpression;

/// <summary><c>for (kind name of right) body</c> (ECMAScript 2015).</summary>
/// <param name="Kind"><c>const</c> or <c>let</c>.</param>
/// <param name="Name">The variable of a pass.</param>
/// <param name="Right">The iterable.</param>
/// <param name="Body">The body.</param>
public sealed record JsForOf(string Kind, string Name, JsExpression Right, JsStatement Body) : JsStatement;

/// <summary><c>label: body</c>: a statement a <see cref="JsLabeledContinue"/> inside it names.</summary>
/// <param name="Label">The label.</param>
/// <param name="Body">The labeled statement, a loop.</param>
public sealed record JsLabeled(string Label, JsStatement Body) : JsStatement;

/// <summary><c>continue label;</c>: the next pass of the labeled loop, from inside loops of its own.</summary>
/// <param name="Label">The label of the loop.</param>
public sealed record JsLabeledContinue(string Label) : JsStatement;
