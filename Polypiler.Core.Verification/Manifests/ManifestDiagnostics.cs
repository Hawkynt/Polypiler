namespace Polypiler.Core.Verification.Manifests;

/// <summary>The diagnostics of the output manifest, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class ManifestDiagnostics {

  /// <summary><c>PLY0053</c>: a manifest is not JSON, fails the schema or has an unsupported <c>schemaVersion</c>. Arguments: the path, the JSON pointer, the reason.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Invalid = new("PLY0053", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "manifest '{0}' is invalid: {1}: {2}", "manifest.schema");

  /// <summary><c>PLY0054</c>: the written state is higher than the evidence supports. Arguments: the path, the written state, the derived state, what is missing.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor StateUnsupported = new("PLY0054", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "manifest '{0}' claims '{1}' but the evidence supports only '{2}': {3}", "manifest.state");

}
