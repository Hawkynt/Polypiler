using System;

namespace Polypiler.Core.Verification.Proofs;

/// <summary>What a prover may spend on one obligation.</summary>
/// <param name="MaxInputBits">
/// The most input bits an exhaustive proof enumerates: the sum of the widths of the inputs. Beyond it
/// nothing is evaluated.
/// </param>
/// <param name="SolverTimeout">
/// The time a solver may take for one obligation, over all its widths; <see langword="null"/> stands
/// for <see cref="DefaultSolverTimeout"/>, and zero lets the solver take none. Only a solver reads it.
/// </param>
public sealed record ProofBudget(int MaxInputBits = 24, TimeSpan? SolverTimeout = null) {

  /// <summary>The time a solver gets when the budget names none: five seconds.</summary>
  public static readonly TimeSpan DefaultSolverTimeout = TimeSpan.FromSeconds(5);

  /// <summary>The budget every prover uses unless told otherwise.</summary>
  public static ProofBudget Default { get; } = new();

  /// <summary>The solver's time for one obligation, with <see langword="null"/> read as <see cref="DefaultSolverTimeout"/>.</summary>
  public TimeSpan EffectiveSolverTimeout => this.SolverTimeout ?? DefaultSolverTimeout;

}
