namespace Polypiler.Core.Backends.CSharp.Syntax;

/// <summary><c>await operand</c> (C# 5).</summary>
/// <param name="Operand">The task awaited.</param>
public sealed record CsAwait(CsExpression Operand) : CsExpression;
