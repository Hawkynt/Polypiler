using System;
using System.Globalization;
using System.Linq;

namespace Polypiler.Core.Verification.Proofs;

/// <summary>
/// The diagnostics of the provers, as <c>spec/diagnostics.yaml</c> registers them, and the line a
/// verdict adds to the optimisation report.
/// </summary>
public static class ProofDiagnostics {

  /// <summary>
  /// <c>PLY0055</c>: a proof gave no verdict - the preconditions exclude every input, or the solver
  /// timed out or answered unknown - so the rewrite is not applied. Arguments: the rule, the reason.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Inconclusive = new("PLY0055", DiagnosticSeverity.Warning, null, "proof of {0} inconclusive ({1}); rewrite not applied", "verification.proof.inconclusive");

  /// <summary>
  /// <c>PLY0056</c>: a prover found an input on which the rewrite changes the value. Arguments: the
  /// rule, <c> at width &lt;w&gt;</c> or nothing, the inputs, the before value, the after value.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Refuted = new("PLY0056", DiagnosticSeverity.Error, ExitCode.SemanticMismatch, "proof of {0} refuted{1}: {2}: before {3}, after {4}", "verification.proof.refuted");

  /// <summary><c>PLY0057</c>: an exhaustive proof needs more input bits than its budget. Arguments: the rule, the bits, the budget.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor DomainTooLarge = new("PLY0057", DiagnosticSeverity.Error, ExitCode.BudgetExceeded, "exhaustive proof of {0} needs {1} input bits, budget is {2}", "verification.proof.budget");

  /// <summary><c>PLY0062</c>: an obligation uses an operation the prover does not model. Arguments: the rule, the operation, the prover.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unmodelled = new("PLY0062", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "proof of {0}: operation '{1}' is not modelled by prover '{2}'", "verification.proof.model");

  /// <summary>The diagnostic a verdict raises; <see langword="null"/> for <see cref="ProofResult.Proved"/>, which raises none.</summary>
  /// <param name="rule">The rule the proof is for.</param>
  /// <param name="prover">The prover's <see cref="IEquivalenceProver.Id"/>.</param>
  /// <param name="result">The verdict.</param>
  /// <returns>The diagnostic, or <see langword="null"/>.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static Diagnostic? For(string rule, string prover, ProofResult result) {
    ArgumentNullException.ThrowIfNull(rule);
    ArgumentNullException.ThrowIfNull(prover);
    ArgumentNullException.ThrowIfNull(result);
    return result switch {
      ProofResult.Proved => null,
      ProofResult.Refuted refuted => Diagnostic.Create(Refuted, null, rule,
        refuted.Width > 0 ? $" at width {refuted.Width.ToString(CultureInfo.InvariantCulture)}" : "",
        Counterexample(refuted), Hex(refuted.BeforeValue), After(refuted)),
      ProofResult.Inconclusive inconclusive => Diagnostic.Create(Inconclusive, null, rule, inconclusive.Reason),
      ProofResult.BudgetExceeded exceeded => Diagnostic.Create(DomainTooLarge, null, rule, exceeded.InputBits, exceeded.Budget),
      ProofResult.Unsupported unsupported => Diagnostic.Create(Unmodelled, null, rule, unsupported.Operation, prover),
      _ => throw new ArgumentException($"Unknown verdict {result.GetType().Name}.", nameof(result))
    };
  }

  /// <summary>
  /// The line a verdict adds to the optimisation report: <c>&lt;rule&gt; &lt;proof-class&gt; proved
  /// &lt;count&gt; cases</c>, or <c>&lt;rule&gt; &lt;proof-class&gt; &lt;verdict&gt;: &lt;detail&gt;</c>.
  /// </summary>
  /// <param name="rule">The rule the proof is for.</param>
  /// <param name="result">The verdict.</param>
  /// <returns>The line, without a line break.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static string ReportLine(string rule, ProofResult result) {
    ArgumentNullException.ThrowIfNull(rule);
    ArgumentNullException.ThrowIfNull(result);
    var detail = result switch {
      ProofResult.Proved proved => $"proved {proved.Evaluated.ToString(CultureInfo.InvariantCulture)} cases",
      ProofResult.Refuted refuted => $"refuted: {Counterexample(refuted)}: before {Hex(refuted.BeforeValue)}, after {After(refuted)}"
        + (refuted.Width > 0 ? $" at width {refuted.Width.ToString(CultureInfo.InvariantCulture)}" : ""),
      ProofResult.Inconclusive inconclusive => $"inconclusive: {inconclusive.Reason}",
      ProofResult.BudgetExceeded exceeded => $"budget-exceeded: {exceeded.InputBits.ToString(CultureInfo.InvariantCulture)} input bits, budget is {exceeded.Budget.ToString(CultureInfo.InvariantCulture)}",
      ProofResult.Unsupported unsupported => $"unsupported: {unsupported.Operation}",
      _ => throw new ArgumentException($"Unknown verdict {result.GetType().Name}.", nameof(result))
    };
    return $"{rule} {result.ProofClass} {detail}";
  }

  /// <summary>A bit pattern as the diagnostics write it: <c>0x</c> and two hex digits per byte, at least two.</summary>
  /// <param name="value">The bits.</param>
  /// <returns>The text, such as <c>0x80</c> or <c>0x0101</c>.</returns>
  public static string Hex(ulong value) {
    var digits = value.ToString("X", CultureInfo.InvariantCulture);
    return "0x" + digits.PadLeft(digits.Length + (digits.Length & 1), '0');
  }

  private static string Counterexample(ProofResult.Refuted refuted)
    => string.Join(", ", refuted.Inputs.Select(input => $"{input.Key}={Hex(input.Value)}"));

  private static string After(ProofResult.Refuted refuted) => refuted.AfterFault is { } fault ? $"invalid ({fault})" : Hex(refuted.AfterValue);

}
