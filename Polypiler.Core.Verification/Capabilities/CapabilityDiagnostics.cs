namespace Polypiler.Core.Verification.Capabilities;

/// <summary>The diagnostics of the capability report, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class CapabilityDiagnostics {

  /// <summary>
  /// <c>PLY0061</c>: a capability is claimed without evidence: a committed cell above the state its
  /// gate records prove, or a gate record of a pair that has no language or target document.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unsupported = new("PLY0061", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "capabilities.evidence");

}
