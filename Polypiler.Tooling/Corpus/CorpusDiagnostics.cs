using System;
using System.Collections.Generic;
using Polypiler.Core;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Corpus;

/// <summary>The diagnostics of the corpus importer, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class CorpusDiagnostics {

  /// <summary>A corpus file's SHA-256 or length differs from the lock, or the file is missing.</summary>
  public const string HashMismatch = "PLY0023";

  /// <summary>The registered diagnostic of <see cref="HashMismatch"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor HashMismatchDescriptor = new(HashMismatch, DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "corpus.lock");

  /// <summary>A path does not exist at the pinned commit, the commit cannot be obtained, or a corpus file cannot be written.</summary>
  public const string PathInvalid = "PLY0025";

  /// <summary>The registered diagnostic of <see cref="PathInvalid"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor PathInvalidDescriptor = new(PathInvalid, DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "corpus.path");

  /// <summary><c>corpus.yaml</c> or the lock is invalid, or the workspace holds a file the lock does not list.</summary>
  public const string ManifestInvalid = "PLY0026";

  /// <summary>The registered diagnostic of <see cref="ManifestInvalid"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ManifestInvalidDescriptor = new(ManifestInvalid, DiagnosticSeverity.Error, ExitCode.EnvironmentError, "corpus manifest invalid: {0}", "corpus.manifest");

  /// <summary>A <see cref="HashMismatch"/> diagnostic.</summary>
  public static Diagnostic Mismatch(string message) => Diagnostic.Create(HashMismatchDescriptor, null, message);

  /// <summary>A <see cref="PathInvalid"/> diagnostic, with what git or the file system said as its cause.</summary>
  public static Diagnostic Path(string message, string cause)
    => Diagnostic.Create(PathInvalidDescriptor, null, $"{message}: {cause}") with { Cause = cause };

  /// <summary>A <see cref="ManifestInvalid"/> diagnostic, positioned in a file when a line is known.</summary>
  public static Diagnostic Manifest(string reason, string? file = null, int line = 0, int column = 0)
    => Diagnostic.Create(ManifestInvalidDescriptor, file is null || line < 1 ? null : new SourceRange(file, line, Math.Max(column, 1), line, Math.Max(column, 1)), reason);

  /// <summary>A <c>PLY0021</c> diagnostic: git cannot be started.</summary>
  public static Diagnostic GitMissing(string executable, string cause)
    => Diagnostic.Create(ToolchainDiagnostic.NotInstalledDescriptor, null, $"toolchain 'git' is not installed (probe: {executable} --version): {cause}") with { Cause = cause };

}

/// <summary>Thrown when the corpus cannot be loaded, imported or verified; carries every diagnostic found.</summary>
public sealed class CorpusException : Exception {

  /// <summary>Creates the exception for one or more diagnostics.</summary>
  public CorpusException(IReadOnlyList<Diagnostic> diagnostics)
    : base(string.Join(Environment.NewLine, diagnostics)) => this.Diagnostics = diagnostics;

  /// <summary>Creates the exception for one diagnostic.</summary>
  public CorpusException(Diagnostic diagnostic)
    : this([diagnostic]) { }

  /// <summary>Everything that is wrong, in the order it was found.</summary>
  public IReadOnlyList<Diagnostic> Diagnostics { get; }

}
