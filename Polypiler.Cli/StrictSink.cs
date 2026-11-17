using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using Polypiler.Core;

namespace Polypiler.Cli;

/// <summary>
/// The sink of <c>--strict</c> (PRD §6.1, ADR 0003): every warning whose registry entry in
/// <c>spec/diagnostics.yaml</c> has <c>strict: true</c> reaches the sink it wraps as the error
/// <c>PLY0010</c>, which carries the original code and message; any other diagnostic passes unchanged.
/// Strict mode is no "warnings as errors": a warning not marked <c>strict</c> stays a warning.
/// </summary>
/// <param name="inner">The sink the diagnostics go to.</param>
public sealed class StrictSink(IDiagnosticSink inner) : IDiagnosticSink {

  private readonly IDiagnosticSink _inner = inner ?? throw new ArgumentNullException(nameof(inner));

  /// <summary>The codes of the warnings <c>--strict</c> refuses: those the registry marks <c>strict: true</c>.</summary>
  public static IReadOnlySet<string> StrictCodes { get; } = new[] { PipelineOptionDiagnostics.TrustedAssumption.Code }.ToFrozenSet(StringComparer.Ordinal);

  /// <inheritdoc/>
  /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is <see langword="null"/>.</exception>
  public void Report(Diagnostic diagnostic) => this._inner.Report(Promote(diagnostic));

  /// <summary>The diagnostic as strict mode reports it: <c>PLY0010</c> in place of a strict warning, at the same place; any other unchanged.</summary>
  /// <param name="diagnostic">The diagnostic.</param>
  /// <returns>The diagnostic to report.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is <see langword="null"/>.</exception>
  public static Diagnostic Promote(Diagnostic diagnostic) {
    ArgumentNullException.ThrowIfNull(diagnostic);
    if (diagnostic.Severity != DiagnosticSeverity.Warning || !StrictCodes.Contains(diagnostic.Code))
      return diagnostic;

    var refused = PipelineOptionDiagnostics.RefusedByStrict;
    return diagnostic with {
      Code = refused.Code,
      Severity = refused.Severity,
      Message = string.Format(CultureInfo.InvariantCulture, refused.MessageFormat, diagnostic.Code, diagnostic.Message),
      ExitCode = refused.ExitCode,
      Rule = refused.Rule
    };
  }

}
