namespace Polypiler.Core;

/// <summary>How serious a <see cref="Diagnostic"/> is.</summary>
public enum DiagnosticSeverity {

  /// <summary>The input was processed; the message points at something worth a look.</summary>
  Warning,

  /// <summary>The input could not be processed.</summary>
  Error

}

/// <summary>One message about an input, with its registered code and, where known, its position.</summary>
/// <param name="Code">The registered code, for example <c>PLY1002</c>.</param>
/// <param name="Severity">How serious the message is.</param>
/// <param name="Message">The text of the message, without code and position.</param>
/// <param name="File">The file the message is about, or <see langword="null"/> when it has none.</param>
/// <param name="Line">The 1-based line, or 0 when the message has no position.</param>
/// <param name="Column">The 1-based column, or 0 when the message has no position.</param>
public sealed record Diagnostic(string Code, DiagnosticSeverity Severity, string Message, string? File = null, int Line = 0, int Column = 0) {

  /// <summary>Formats the diagnostic as <c>file(line,column): severity CODE: message</c>.</summary>
  public override string ToString() {
    var severity = this.Severity == DiagnosticSeverity.Error ? "error" : "warning";
    var text = $"{severity} {this.Code}: {this.Message}";
    if (this.File is null)
      return text;

    return this.Line > 0 ? $"{this.File}({this.Line},{this.Column}): {text}" : $"{this.File}: {text}";
  }

}
