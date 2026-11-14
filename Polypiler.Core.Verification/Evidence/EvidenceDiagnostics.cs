namespace Polypiler.Core.Verification.Evidence;

/// <summary>The diagnostics of the evidence bundle, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class EvidenceDiagnostics {

  /// <summary>
  /// <c>PLY0060</c>: a bundle cannot be read, breaks <c>spec/schemas/evidence.schema.json</c>, lacks an
  /// item its scenario's tags require, or contradicts its outcome. Arguments: the bundle directory and
  /// the reason: <c>is incomplete: missing &lt;item&gt;[, &lt;item&gt;…]</c>,
  /// <c>contradicts its outcome: &lt;n&gt; vector(s) failed</c> or <c>is invalid: &lt;JSON pointer&gt;: &lt;reason&gt;</c>.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Invalid = new("PLY0060", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "evidence bundle '{0}' {1}", "evidence.complete");

}
