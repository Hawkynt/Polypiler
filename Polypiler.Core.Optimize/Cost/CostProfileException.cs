using System;

namespace Polypiler.Core.Optimize.Cost;

/// <summary>The diagnostics of the cost model, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class CostDiagnostics {

  /// <summary><c>PLY0355</c>: the selected target profile has no <c>cost.yaml</c>. Argument: the target profile, <c>&lt;language&gt;/&lt;profile&gt;</c>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ProfileMissing = new("PLY0355", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "target profile '{0}' has no cost profile", "opt-cost.profile-missing");

  /// <summary><c>PLY0356</c>: a cost profile is malformed. Arguments: the file, the key path, the problem.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ProfileInvalid = new("PLY0356", DiagnosticSeverity.Error, ExitCode.InvalidSource, "cost profile '{0}': {1}: {2}", "opt-cost.profile-invalid");

}

/// <summary>A cost profile that is missing or malformed, carrying its diagnostic <c>PLY0355</c> or <c>PLY0356</c>.</summary>
public sealed class CostProfileException : Exception {

  /// <summary>Creates the exception.</summary>
  /// <param name="diagnostic">The diagnostic.</param>
  public CostProfileException(Diagnostic diagnostic) : base(diagnostic.Message) => this.Diagnostic = diagnostic;

  /// <summary>The diagnostic.</summary>
  public Diagnostic Diagnostic { get; }

}
