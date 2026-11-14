namespace Polypiler.Core.Verification.Emulators;

/// <summary>The diagnostics of the PC-BASIC adapter, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class PcBasicDiagnostics {

  /// <summary>
  /// <c>PLY3960</c>: PC-BASIC did not do what it was asked - it exited non-zero, hit a runner limit
  /// (the cause names the runner's <c>PLY0031</c>, <c>PLY0032</c> or <c>PLY0033</c>), wrote no
  /// conversion, or wrote an empty one for an input that is not empty. Arguments: the operation, the cause.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Failed = new("PLY3960", DiagnosticSeverity.Error, ExitCode.OracleFailure, "PC-BASIC {0} failed: {1}", "gwbasic.oracle.pcbasic");

}
