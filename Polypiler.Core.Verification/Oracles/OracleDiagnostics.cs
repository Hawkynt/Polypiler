namespace Polypiler.Core.Verification.Oracles;

/// <summary>The diagnostics of the oracle and differential comparison framework, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class OracleDiagnostics {

  /// <summary>
  /// <c>PLY0050</c>: the reference failed - it did not run, hit a limit, printed something that is not
  /// an output, or disagrees with its own published vectors. Arguments: the oracle, the fixture, the cause.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor OracleFailed = new("PLY0050", DiagnosticSeverity.Error, ExitCode.OracleFailure, "reference oracle '{0}' failed for '{1}': {2}", "verification.oracle.failed");

  /// <summary>
  /// <c>PLY0051</c>: an output differs from the reference where it must be equal. Arguments: the
  /// fixture, the vector, the reference and the actual output in hexadecimal, the first differing byte.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ExactMismatch = new("PLY0051", DiagnosticSeverity.Error, ExitCode.SemanticMismatch, "'{0}' vector '{1}': expected {2}, actual {3}, first difference at byte {4}", "verification.compare.exact");

  /// <summary>
  /// <c>PLY0052</c>: an element is outside the tolerance of the policy. Arguments: the fixture, the
  /// vector, the element's index, its reference and actual bits in hexadecimal, and the reason:
  /// <c>&lt;distance&gt; exceeds &lt;mode&gt; &lt;bound&gt;</c> or <c>special values differ under &lt;rule&gt;</c>.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ToleranceExceeded = new("PLY0052", DiagnosticSeverity.Error, ExitCode.SemanticMismatch, "'{0}' vector '{1}' element {2}: expected {3}, actual {4}, {5}", "verification.compare.tolerance");

  /// <summary><c>PLY0063</c>: no oracle that Polypiler did not produce is registered for a fixture. Argument: the fixture.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NoIndependentOracle = new("PLY0063", DiagnosticSeverity.Error, ExitCode.OracleFailure, "no independent oracle is registered for '{0}'", "verification.oracle.independent");

}
