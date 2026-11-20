namespace Polypiler.Core.Verification.Proofs;

/// <summary>
/// Decides whether two IR fragments compute the same value wherever the first one is valid: the
/// question every rewrite and recognition rule has to answer before it may fire (PRD §4.5, §7.2).
/// A prover never changes the IR it is given, and anything but <see cref="ProofResult.Proved"/>
/// means that the rewrite stays switched off.
/// </summary>
public interface IEquivalenceProver {

  /// <summary>The prover's name in diagnostics and reports, such as <c>exhaustive</c> or <c>smt</c>.</summary>
  string Id { get; }

  /// <summary>Answers an obligation within a budget.</summary>
  /// <param name="obligation">The before and after fragments, their inputs and preconditions.</param>
  /// <param name="budget">How much the prover may spend.</param>
  /// <returns>The verdict.</returns>
  ProofResult Prove(ProofObligation obligation, ProofBudget budget);

}
