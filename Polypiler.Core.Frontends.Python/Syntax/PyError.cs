using System;

namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>
/// An error CPython raises while it parses or compiles a source: the exception class
/// (<c>SyntaxError</c>, <c>IndentationError</c>, <c>TabError</c>, <c>ValueError</c>, <c>MemoryError</c>),
/// the message, and the range as <c>SyntaxError</c> carries it - 1-based lines and 1-based columns
/// counted in characters.
/// </summary>
internal sealed class PyError(string type, string message, int line, int column, int endLine, int endColumn) : Exception(message) {

  /// <summary>The class of the exception, such as <c>SyntaxError</c>.</summary>
  public string Type { get; } = type;

  /// <summary>The message without the class.</summary>
  public string Text { get; } = message;

  /// <summary>The line of the error.</summary>
  public int Line { get; } = line;

  /// <summary>The 1-based character column of the error.</summary>
  public int Column { get; } = column;

  /// <summary>The line the error ends on.</summary>
  public int EndLine { get; } = endLine;

  /// <summary>The 1-based character column the error ends at.</summary>
  public int EndColumn { get; } = endColumn;

}
