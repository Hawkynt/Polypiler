namespace Polypiler.Core.IR;

/// <summary>The diagnostics of source locations and their provenance.</summary>
public static class SourceMapDiagnostics {

  /// <summary>PLY0115: a statement of a module that declares its sources has no origin and is not marked synthetic.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MissingOrigin = new("PLY0115", DiagnosticSeverity.Error, ExitCode.InternalError,
    "instruction {0} in '@{1}' has no origin and is not marked synthetic", "ir.sourcemap.origin");

  /// <summary>PLY0116: a location, a source or a table of sources that is malformed.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MalformedLocation = new("PLY0116", DiagnosticSeverity.Error, ExitCode.InvalidSource,
    "malformed location '{0}'", "ir.sourcemap.syntax");

  /// <summary>PLY0117: a location names a source the module does not declare.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UndeclaredSource = new("PLY0117", DiagnosticSeverity.Error, ExitCode.InvalidSource,
    "location refers to source {0}, but the module declares {1} sources", "ir.sourcemap.source");

}
