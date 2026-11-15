namespace Polypiler.Core.Verification.Gates;

/// <summary>The diagnostics of the gate records, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class GateDiagnostics {

  /// <summary>
  /// <c>PLY0058</c>: a gate record is rejected. Arguments: the file, and either
  /// <c>fixture '&lt;fixture&gt;' claims '&lt;state&gt;': &lt;reason&gt;</c> or what is wrong with the record as a whole.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Invalid = new("PLY0058", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "gate record '{0}': {1}", "gates.record");

}
