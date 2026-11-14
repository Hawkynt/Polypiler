using System;
using System.Collections.Generic;

namespace Polypiler.Core;

/// <summary>Receives the diagnostics a stage reports.</summary>
public interface IDiagnosticSink {

  /// <summary>Reports one diagnostic.</summary>
  /// <param name="diagnostic">The diagnostic.</param>
  void Report(Diagnostic diagnostic);

}

/// <summary>
/// Keeps the diagnostics of a run in the order they were reported, and derives the run's exit code
/// from them by the rule of ADR 0003.
/// </summary>
public sealed class DiagnosticBag : IDiagnosticSink {

  private readonly List<Diagnostic> _items = [];

  /// <summary>Every diagnostic reported, in emission order.</summary>
  public IReadOnlyList<Diagnostic> Items => this._items;

  /// <summary>How many errors were reported.</summary>
  public int ErrorCount { get; private set; }

  /// <summary>How many warnings were reported.</summary>
  public int WarningCount { get; private set; }

  /// <summary>
  /// The exit code: <see cref="Core.ExitCode.Success"/> without an error; otherwise the exit code of the
  /// first error in emission order, where an error without one counts as an internal error; and
  /// <see cref="Core.ExitCode.InternalError"/> regardless of order once an internal compiler error
  /// (<c>PLY0001</c>) was reported. Warnings and infos never change it.
  /// </summary>
  public ExitCode ExitCode { get; private set; } = ExitCode.Success;

  /// <inheritdoc/>
  /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is <see langword="null"/>.</exception>
  public void Report(Diagnostic diagnostic) {
    ArgumentNullException.ThrowIfNull(diagnostic);
    this._items.Add(diagnostic);
    if (diagnostic.Code == DiagnosticDescriptor.InternalError.Code)
      this.ExitCode = ExitCode.InternalError;

    switch (diagnostic.Severity) {
      case DiagnosticSeverity.Warning:
        ++this.WarningCount;
        break;
      case DiagnosticSeverity.Error:
        if (++this.ErrorCount == 1 && this.ExitCode == ExitCode.Success)
          this.ExitCode = diagnostic.ExitCode ?? ExitCode.InternalError;
        break;
    }
  }

}
