using System;

namespace Polypiler.Core;

/// <summary>A range of a source file: 1-based lines and columns, the end inclusive.</summary>
public readonly record struct SourceRange {

  /// <summary>Creates a range.</summary>
  /// <param name="file">The file as it was named.</param>
  /// <param name="startLine">The first line, from 1.</param>
  /// <param name="startColumn">The first column, from 1.</param>
  /// <param name="endLine">The last line, not before <paramref name="startLine"/>.</param>
  /// <param name="endColumn">The last column, inclusive; on the start line not before <paramref name="startColumn"/>.</param>
  /// <exception cref="ArgumentNullException"><paramref name="file"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentOutOfRangeException">A value is below 1, or the end lies before the start.</exception>
  public SourceRange(string file, int startLine, int startColumn, int endLine, int endColumn) {
    ArgumentNullException.ThrowIfNull(file);
    ArgumentOutOfRangeException.ThrowIfLessThan(startLine, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(startColumn, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(endLine, startLine);
    ArgumentOutOfRangeException.ThrowIfLessThan(endColumn, endLine == startLine ? startColumn : 1);
    this.File = file;
    this.StartLine = startLine;
    this.StartColumn = startColumn;
    this.EndLine = endLine;
    this.EndColumn = endColumn;
  }

  /// <summary>The file as it was named.</summary>
  public string File { get; }

  /// <summary>The first line, from 1.</summary>
  public int StartLine { get; }

  /// <summary>The first column, from 1.</summary>
  public int StartColumn { get; }

  /// <summary>The last line.</summary>
  public int EndLine { get; }

  /// <summary>The last column, inclusive.</summary>
  public int EndColumn { get; }

}
