using System;
using System.Collections.Generic;

namespace Polypiler.Core.Verification.Proofs;

/// <summary>What a solver answered to a query.</summary>
public enum SmtStatus {

  /// <summary>No assignment satisfies the query: for a prover's query, the rewrite holds.</summary>
  Unsat,

  /// <summary>An assignment satisfies it: the model is a candidate counterexample.</summary>
  Sat,

  /// <summary>The solver gave up, or ran out of time.</summary>
  Unknown

}

/// <summary>A solver's answer.</summary>
/// <param name="Status">Satisfiable, unsatisfiable or unknown.</param>
/// <param name="Model">For <see cref="SmtStatus.Sat"/> the value of each declared constant as a bit pattern (a truth value as 0 or 1); empty otherwise.</param>
/// <param name="Reason">For <see cref="SmtStatus.Unknown"/> the solver's reason, <c>timeout</c> when time ran out; <see langword="null"/> otherwise.</param>
public sealed record SmtAnswer(SmtStatus Status, IReadOnlyDictionary<string, ulong> Model, string? Reason = null);

/// <summary>Decides SMT-LIB queries; <see cref="Z3Solver"/> is the one Polypiler ships.</summary>
public interface ISmtSolver {

  /// <summary>The solver's name and version, for the evidence.</summary>
  string Version { get; }

  /// <summary>Decides one query.</summary>
  /// <param name="query">The SMT-LIB script, declarations and assertions, ending in <c>(check-sat)</c>.</param>
  /// <param name="constants">The names of the constants whose values a model is to give.</param>
  /// <param name="timeout">The most time the solver may take.</param>
  /// <returns>The answer.</returns>
  SmtAnswer Check(string query, IReadOnlyList<string> constants, TimeSpan timeout);

}
