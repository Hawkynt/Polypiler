using System.Collections.Generic;

namespace Polypiler.Core.Verification.Proofs;

/// <summary>
/// The verdict of a prover on one obligation. Only <see cref="Proved"/> lets a rewrite fire; every
/// other verdict leaves the IR as it is.
/// </summary>
/// <param name="ProofClass">
/// The proof class the verdict belongs to, such as <c>exhaustive-8bit</c> or <c>smt-bitvector</c>;
/// a rule names the class it needs.
/// </param>
public abstract record ProofResult(string ProofClass) {

  /// <summary>Every input that satisfies the preconditions and keeps the before side valid gives the same bits on both sides.</summary>
  /// <param name="ProofClass">The proof class.</param>
  /// <param name="Evaluated">How many input combinations were compared; for a solver, how many queries answered <c>unsat</c>.</param>
  public sealed record Proved(string ProofClass, long Evaluated) : ProofResult(ProofClass);

  /// <summary>
  /// An input for which the before side is valid and the after side is invalid or gives other bits:
  /// the first in enumeration order, or the solver's model after evaluation confirmed it.
  /// </summary>
  /// <param name="ProofClass">The proof class.</param>
  /// <param name="Inputs">The bit pattern of each input, by name, in the order of the obligation.</param>
  /// <param name="BeforeValue">The bit pattern the before side computes.</param>
  /// <param name="AfterValue">The bit pattern the after side computes; 0 when it is invalid.</param>
  public sealed record Refuted(string ProofClass, IReadOnlyDictionary<string, ulong> Inputs, ulong BeforeValue, ulong AfterValue) : ProofResult(ProofClass) {

    /// <summary>Why the after side has no value for the inputs, such as a strict shift by the width; <see langword="null"/> when it has one.</summary>
    public string? AfterFault { get; init; }

    /// <summary>The width the counterexample was found at, for a prover that tries several; 0 for one that does not.</summary>
    public int Width { get; init; }

  }

  /// <summary>Nothing was shown either way: the preconditions exclude every input, or the solver gave up.</summary>
  /// <param name="ProofClass">The proof class.</param>
  /// <param name="Reason">Why, such as <c>timeout</c> or <c>vacuous: no input satisfies the preconditions</c>.</param>
  public sealed record Inconclusive(string ProofClass, string Reason) : ProofResult(ProofClass);

  /// <summary>The obligation uses an operation the prover does not model: floating point, memory, calls, effects.</summary>
  /// <param name="ProofClass">The proof class.</param>
  /// <param name="Operation">The operation, as the obligation writes it.</param>
  public sealed record Unsupported(string ProofClass, string Operation) : ProofResult(ProofClass) {

    /// <summary>What the parser said about the operation, when it could not read it at all; <see langword="null"/> otherwise.</summary>
    public string? Detail { get; init; }

  }

  /// <summary>The inputs together have more bits than the budget allows enumerating; nothing was evaluated.</summary>
  /// <param name="ProofClass">The proof class.</param>
  /// <param name="InputBits">The sum of the input widths.</param>
  public sealed record BudgetExceeded(string ProofClass, int InputBits) : ProofResult(ProofClass) {

    /// <summary>The budget the inputs exceeded.</summary>
    public int Budget { get; init; }

  }

}
