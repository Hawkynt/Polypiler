namespace Polypiler.Core.Backends.JavaScript.Syntax;

/// <summary><c>await argument</c> (ECMAScript 2017).</summary>
/// <param name="Argument">The value awaited.</param>
public sealed record JsAwait(JsExpression Argument) : JsExpression;
