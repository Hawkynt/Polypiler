using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Polypiler.Core.Verification.Oracles;

/// <summary>What computed the reference values of an oracle (PRD §23.1).</summary>
public enum OracleKind {

  /// <summary>Values published with the algorithm: a vector file.</summary>
  PublishedVectors,

  /// <summary>The original program, executed with its own toolchain.</summary>
  SourceToolchain,

  /// <summary>The original program, executed in an emulator of its machine.</summary>
  Emulator,

  /// <summary>A proof; the proof-based oracles add it.</summary>
  Prover

}

/// <summary>
/// An implementation Polypiler did not write that computes the reference outputs of a fixture: the
/// one side of a differential comparison that decides what "correct" means.
/// </summary>
public interface IVerificationOracle {

  /// <summary>The oracle's name in a diagnostic.</summary>
  string Id { get; }

  /// <summary>What kind of reference it is.</summary>
  OracleKind Kind { get; }

  /// <summary>What computed the reference: <c>rfc1950</c>, <c>node</c>, <c>vice</c>, …; never <c>polypiler</c>.</summary>
  string Producer { get; }

  /// <summary>The file the reference comes from, a vector file or a program; <see langword="null"/> when it has none.</summary>
  string? ReferenceFile { get; }

  /// <summary>Computes the reference output of every vector.</summary>
  /// <param name="request">The fixture and its vectors.</param>
  /// <param name="cancellationToken">Ends the evaluation.</param>
  /// <returns>One output per vector in vector order, or no outputs and the <c>PLY0050</c> that says why.</returns>
  Task<OracleResult> EvaluateAsync(OracleRequest request, CancellationToken cancellationToken = default);

}

/// <summary>What an oracle is asked to compute.</summary>
/// <param name="FixtureId">The fixture's ID, as a diagnostic names it.</param>
/// <param name="Vectors">The vectors, in file order.</param>
public sealed record OracleRequest(string FixtureId, IReadOnlyList<TestVector> Vectors) {

  /// <summary>The fixture's ID.</summary>
  public string FixtureId { get; } = FixtureId ?? throw new ArgumentNullException(nameof(FixtureId));

  /// <summary>The vectors, in file order.</summary>
  public IReadOnlyList<TestVector> Vectors { get; } = Vectors?.ToArray() ?? throw new ArgumentNullException(nameof(Vectors));

}

/// <summary>What an oracle computed.</summary>
/// <param name="Outputs">One output per vector in vector order; empty when the oracle failed.</param>
/// <param name="Diagnostics">Why the oracle failed; empty when it did not.</param>
public sealed record OracleResult(IReadOnlyList<byte[]> Outputs, IReadOnlyList<Diagnostic> Diagnostics) {

  /// <summary>Whether the oracle produced its outputs.</summary>
  public bool Succeeded => this.Diagnostics.Count == 0;

  /// <summary>The result of an oracle that failed for a fixture.</summary>
  /// <param name="oracle">The oracle's ID.</param>
  /// <param name="fixture">The fixture's ID.</param>
  /// <param name="cause">Why.</param>
  /// <returns>No outputs and one <c>PLY0050</c>.</returns>
  public static OracleResult Failed(string oracle, string fixture, string cause)
    => new([], [Diagnostic.Create(OracleDiagnostics.OracleFailed, null, oracle, fixture, cause)]);

}
