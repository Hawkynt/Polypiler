using System.Collections.Generic;
using System.Linq;
using Polypiler.Core;
using Polypiler.Tooling.Corpus;

namespace Polypiler.Tooling.Licensing;

/// <summary>The diagnostics of the licence scanner, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class LicenceDiagnostics {

  /// <summary>A licence the mode does not accept, a declared licence outside the detected one, or a restricted asset.</summary>
  public const string NotApproved = "PLY0024";

  /// <summary>The registered diagnostic of <see cref="NotApproved"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NotApprovedDescriptor = new(NotApproved, DiagnosticSeverity.Error, ExitCode.LicenseBlocker, "{0}", "licences.policy");

  /// <summary>No rule yields a licence, the expression does not parse, or a vendored library has no licence file.</summary>
  public const string Undetermined = "PLY0029";

  /// <summary>The registered diagnostic of <see cref="Undetermined"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UndeterminedDescriptor = new(Undetermined, DiagnosticSeverity.Error, ExitCode.LicenseBlocker, "{0}", "licences.detection");

  /// <summary>A <see cref="NotApproved"/> diagnostic.</summary>
  public static Diagnostic Refused(string message) => Diagnostic.Create(NotApprovedDescriptor, null, message);

  /// <summary>A <see cref="Undetermined"/> diagnostic for a path, with why when it is known.</summary>
  public static Diagnostic Unknown(string path, string? reason = null)
    => Diagnostic.Create(UndeterminedDescriptor, null, $"'{path}': no licence could be determined{(reason is null ? "" : $": {reason}")}");

  /// <summary>A <c>PLY0026</c> diagnostic about a licensing file, its reason prefixed with what the file is.</summary>
  public static Diagnostic Invalid(string what, string reason, string? file = null, int line = 0, int column = 0)
    => CorpusDiagnostics.Manifest($"{what}: {reason}", file, line, column);

  /// <summary>
  /// The exit code of a run that found diagnostics: <see cref="ExitCode.Success"/> for none, the
  /// environment error when any diagnostic is one (the run could not look at everything), otherwise
  /// the licence blocker.
  /// </summary>
  public static int ExitCodeOf(IEnumerable<Diagnostic> diagnostics) {
    var codes = diagnostics.Select(diagnostic => diagnostic.ExitCode).ToList();
    if (codes.Count == 0)
      return (int)ExitCode.Success;
    return (int)(codes.Contains(ExitCode.EnvironmentError) ? ExitCode.EnvironmentError : ExitCode.LicenseBlocker);
  }

}
