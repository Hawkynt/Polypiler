using System;
using Polypiler.Core.Frontends.Python.Ast;

namespace Polypiler.Core.Frontends.Python.Model;

/// <summary>A refusal of the import at a node, thrown where the lowering of a function cannot go on.</summary>
/// <param name="diagnostic">Why the import is refused.</param>
internal sealed class Refusal(Diagnostic diagnostic) : Exception(diagnostic.Message) {

  /// <summary>Why the import is refused.</summary>
  public Diagnostic Diagnostic { get; } = diagnostic;

}

/// <summary>The file imported: its name for diagnostics and its text, of which a diagnostic quotes the construct.</summary>
internal sealed class SourceFile {

  private readonly string[] _lines;

  /// <summary>Describes the file.</summary>
  /// <param name="path">The name diagnostics carry.</param>
  /// <param name="text">The text.</param>
  public SourceFile(string path, string text) {
    this.Path = path;
    this._lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
  }

  /// <summary>The name diagnostics carry.</summary>
  public string Path { get; }

  /// <summary>The text of a node as the source writes it; the first line of it, ending in <c>…</c>, for a node of several lines.</summary>
  public string Quote(PyNode node) {
    var line = this._lines[node.Line - 1];
    var start = node.Column - 1;
    return node.EndLine != node.Line ? line[start..].TrimEnd() + "…" : line[start..(node.EndColumn - 1)];
  }

  /// <summary>A diagnostic at a node.</summary>
  public Diagnostic At(PyNode node, DiagnosticDescriptor descriptor, string message)
    => descriptor.At(message, this.Path, node.Line, node.Column) with { EndLine = node.EndLine, EndColumn = Math.Max(1, node.EndColumn - 1) };

  /// <summary>A refusal at a node, to throw.</summary>
  public Refusal Refuse(PyNode node, DiagnosticDescriptor descriptor, string message) => new(this.At(node, descriptor, message));

}
