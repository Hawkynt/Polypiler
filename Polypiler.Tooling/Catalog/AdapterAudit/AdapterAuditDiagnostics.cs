using System;
using System.Collections.Generic;
using Polypiler.Core;

namespace Polypiler.Tooling.Catalog.AdapterAudit;

/// <summary>The diagnostic of the adapter licence audit, as <c>spec/diagnostics.yaml</c> registers it.</summary>
public static class AdapterAuditDiagnostics {

  /// <summary>
  /// A touched path without a record or with a decision other than usable, a stale record, a refused
  /// clearance, or an audit file that is unreadable or breaks its schema or a rule.
  /// </summary>
  public const string Violation = "PLY0950";

  /// <summary>The registered diagnostic of <see cref="Violation"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ViolationDescriptor = new(Violation, DiagnosticSeverity.Error, ExitCode.LicenseBlocker, "{0}", "catalog.adapter-audit");

  /// <summary>A <see cref="Violation"/> diagnostic with a message of its own.</summary>
  public static Diagnostic Create(string message) => Diagnostic.Create(ViolationDescriptor, null, message);

  /// <summary>A <see cref="Violation"/> diagnostic about the audit file, <c>licence audit: &lt;detail&gt;</c>, positioned when a line is known.</summary>
  public static Diagnostic Rule(string detail, string? file = null, int line = 0, int column = 0)
    => Diagnostic.Create(ViolationDescriptor, file is null || line < 1 ? null : new SourceRange(file, line, Math.Max(column, 1), line, Math.Max(column, 1)), $"licence audit: {detail}");

}

/// <summary>Thrown when an audit file cannot be read or is not a valid audit; carries every diagnostic found.</summary>
public sealed class AdapterAuditException : Exception {

  /// <summary>Creates the exception for the diagnostics found.</summary>
  public AdapterAuditException(IReadOnlyList<Diagnostic> diagnostics)
    : base(string.Join(Environment.NewLine, diagnostics)) => this.Diagnostics = diagnostics;

  /// <summary>Everything that is wrong, in the order it was found.</summary>
  public IReadOnlyList<Diagnostic> Diagnostics { get; }

}
