using System;
using System.Collections.Generic;
using Polypiler.Core;

namespace Polypiler.Tooling.Integrity;

/// <summary>The diagnostics of the golden-vector gate, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class IntegrityDiagnostics {

  /// <summary>A golden file differs from the golden lock, is missing from it or from disk, or changed against the pull-request rule.</summary>
  public const string GoldenModified = "PLY0027";

  /// <summary>The registered diagnostic of <see cref="GoldenModified"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor GoldenModifiedDescriptor = new(GoldenModified, DiagnosticSeverity.Error, ExitCode.EnvironmentError, "golden file '{0}' was modified: {1}", "golden.lock");

  /// <summary>A vector file or a provenance marker does not name an independent source.</summary>
  public const string ProvenanceMissing = "PLY0028";

  /// <summary>The registered diagnostic of <see cref="ProvenanceMissing"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ProvenanceMissingDescriptor = new(ProvenanceMissing, DiagnosticSeverity.Error, ExitCode.EnvironmentError, "vectors '{0}' lack independent provenance: {1}", "golden.provenance");

  /// <summary>A <see cref="GoldenModified"/> diagnostic for a file named by its path below the repository root.</summary>
  public static Diagnostic Modified(string path, string reason) => Diagnostic.Create(GoldenModifiedDescriptor, null, path, reason);

  /// <summary>A <see cref="ProvenanceMissing"/> diagnostic for a file named by its path below the repository root.</summary>
  public static Diagnostic Provenance(string path, string reason) => Diagnostic.Create(ProvenanceMissingDescriptor, null, path, reason);

}

/// <summary>Thrown when a golden file or the golden lock cannot be loaded; carries every diagnostic found.</summary>
public sealed class IntegrityException : Exception {

  /// <summary>Creates the exception for one or more diagnostics.</summary>
  public IntegrityException(IReadOnlyList<Diagnostic> diagnostics)
    : base(string.Join(Environment.NewLine, diagnostics)) => this.Diagnostics = diagnostics;

  /// <summary>Everything that is wrong, in the order it was found.</summary>
  public IReadOnlyList<Diagnostic> Diagnostics { get; }

}
