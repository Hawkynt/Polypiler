using System;

namespace Polypiler.Core.Verification.Benchmarks;

/// <summary>
/// A benchmark definition, result or budget file that does not follow its schema; reported as
/// <c>PLY0078</c> with the file, the position and the field.
/// </summary>
public sealed class BenchmarkFileException : Exception {

  /// <summary>Creates the exception.</summary>
  /// <param name="file">The file, as the caller named it.</param>
  /// <param name="line">The 1-based line, or 0 when the position is unknown.</param>
  /// <param name="column">The 1-based column, or 0 when the position is unknown.</param>
  /// <param name="reason">What is wrong, naming the field.</param>
  public BenchmarkFileException(string file, int line, int column, string reason) : base(reason) {
    this.File = file;
    this.Line = line;
    this.Column = column;
  }

  /// <summary>The file.</summary>
  public string File { get; }

  /// <summary>The 1-based line, or 0 when the position is unknown.</summary>
  public int Line { get; }

  /// <summary>The 1-based column, or 0 when the position is unknown.</summary>
  public int Column { get; }

  /// <summary>The problem as the diagnostic <c>PLY0078</c>.</summary>
  public Diagnostic ToDiagnostic()
    => Diagnostic.Create(BenchmarkDiagnostics.FileInvalid, null, this.Message) with { File = this.File, Line = this.Line, Column = this.Column };

}
