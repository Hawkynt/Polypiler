using System.Numerics;

namespace Polypiler.Permutation.Algebra;

/// <summary>
/// Inputs on which a mapping <c>M</c> does not distribute over an operation: <see cref="Routed"/> and
/// <see cref="Combined"/> differ on at least one output bit that is not a don't-care.
/// </summary>
/// <param name="Operation">The operation.</param>
/// <param name="A">The first operand.</param>
/// <param name="B">The second operand; 0 and unused for <see cref="BitwiseOperation.Not"/>.</param>
/// <param name="Routed">The operation applied first and the mapping second: <c>M(a op b)</c>, or <c>M(~a)</c>.</param>
/// <param name="Combined">The mapping applied first and the operation second: <c>M(a) op M(b)</c>, or <c>~M(a)</c>.</param>
public sealed record DistributionCounterexample(BitwiseOperation Operation, BigInteger A, BigInteger B, BigInteger Routed, BigInteger Combined);
