namespace Polypiler.Core.Backends.CSharp.Syntax;

/// <summary><c>yield return value;</c>, or <c>yield break;</c> without a value.</summary>
/// <param name="Value">The value produced, or <see langword="null"/> for the end of the sequence.</param>
public sealed record CsYield(CsExpression? Value) : CsStatement;
