using Polypiler.Core;

namespace Polypiler.Cli.Verify;

/// <summary>The diagnostics of <c>polypiler verify</c>, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class VerifyDiagnostics {

  /// <summary><c>PLY0016</c>: the directory holds no <c>polypiler.manifest.json</c>. Argument: the directory as typed.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NoManifest = new("PLY0016", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "no manifest in '{0}'", "cli.verify.manifest");

  /// <summary><c>PLY0017</c>: the vector file cannot be read as test vectors. Arguments: the file as typed, the reason.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor VectorsNotUnderstood = new("PLY0017", DiagnosticSeverity.Error, ExitCode.UsageError, "'{0}' holds no test vectors: {1}", "cli.verify.vectors");

  /// <summary><c>PLY0021</c>: the toolchain the output runs on is not installed. Argument: the whole message of the registry's form.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ToolchainMissing = new("PLY0021", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "toolchains.installed");

}
