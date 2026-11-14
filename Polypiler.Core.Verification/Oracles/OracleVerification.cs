using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Polypiler.Core.Verification.Oracles;

/// <summary>The result of verifying a candidate against the oracle of its fixture.</summary>
/// <param name="Report">The comparison; <see cref="ComparisonReport.NotCompared"/> when there was no usable reference.</param>
/// <param name="Diagnostics">The <c>PLY0063</c>, <c>PLY0050</c>, or the <c>PLY0051</c> and <c>PLY0052</c> of every vector that failed.</param>
public sealed record VerificationResult(ComparisonReport Report, IReadOnlyList<Diagnostic> Diagnostics) {

  /// <summary>The exit code the diagnostics map to (ADR 0003); <see cref="ExitCode.Success"/> when there are none.</summary>
  public ExitCode ExitCode {
    get {
      var bag = new DiagnosticBag();
      foreach (var diagnostic in this.Diagnostics)
        bag.Report(diagnostic);

      return bag.ExitCode;
    }
  }

}

/// <summary>
/// One differential comparison: resolves the fixture's independent oracle, has it compute the
/// reference, checks the reference against the vectors' published values, and compares the
/// candidate's outputs with it under the policy. A missing or failing reference is an oracle
/// failure, never a mismatch and never a pass.
/// </summary>
public static class OracleVerification {

  /// <summary>Verifies a candidate's outputs.</summary>
  /// <param name="registry">The oracles.</param>
  /// <param name="fixtureId">The fixture's ID.</param>
  /// <param name="vectors">The vectors, in file order.</param>
  /// <param name="actual">The candidate's output of each vector.</param>
  /// <param name="policy">How the outputs are compared.</param>
  /// <param name="cancellationToken">Ends the oracle's evaluation.</param>
  /// <returns>The report and its diagnostics.</returns>
  public static async Task<VerificationResult> VerifyAsync(OracleRegistry registry, string fixtureId, IReadOnlyList<TestVector> vectors, IReadOnlyList<byte[]> actual, ExactnessPolicy policy, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(fixtureId);
    ArgumentNullException.ThrowIfNull(vectors);
    if (registry.Resolve(fixtureId) is not { } oracle)
      return new(ComparisonReport.NotCompared(vectors.Count), [Diagnostic.Create(OracleDiagnostics.NoIndependentOracle, null, fixtureId)]);

    var result = await oracle.EvaluateAsync(new OracleRequest(fixtureId, vectors), cancellationToken).ConfigureAwait(false);
    if (!result.Succeeded)
      return new(ComparisonReport.NotCompared(vectors.Count), result.Diagnostics);

    if (result.Outputs.Count != vectors.Count)
      return Failed(oracle, fixtureId, vectors.Count, $"it computed {result.Outputs.Count} outputs for {vectors.Count} vectors");

    for (var index = 0; index < vectors.Count; ++index)
      if (vectors[index].Expected is { } expected && !expected.AsSpan().SequenceEqual(result.Outputs[index]))
        return Failed(oracle, fixtureId, vectors.Count, $"vector '{vectors[index].Name}': the reference computed {Hex.Format(result.Outputs[index])}, the published value is {Hex.Format(expected)}");

    var report = DifferentialComparer.Compare(vectors, result.Outputs, actual, policy);
    return new(report, report.Diagnostics(fixtureId));
  }

  private static VerificationResult Failed(IVerificationOracle oracle, string fixtureId, int total, string cause)
    => new(ComparisonReport.NotCompared(total), OracleResult.Failed(oracle.Id, fixtureId, cause).Diagnostics);

}
