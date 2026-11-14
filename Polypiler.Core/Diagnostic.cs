using System;
using System.Globalization;

namespace Polypiler.Core;

/// <summary>How serious a <see cref="Diagnostic"/> is (ADR 0003).</summary>
public enum DiagnosticSeverity {

  /// <summary>The input was processed; the message only informs. It never changes the exit code.</summary>
  Info,

  /// <summary>The input was processed; the message points at something worth a look.</summary>
  Warning,

  /// <summary>The input could not be processed.</summary>
  Error

}

/// <summary>
/// One message about an input, with its registered code and, where known, its position. The
/// positional members are what every stage reports; the init-only members carry the rest of the
/// structured record of ADR 0003 and are filled by <see cref="Create"/> from a
/// <see cref="DiagnosticDescriptor"/> and by the command that knows the invocation.
/// </summary>
/// <param name="Code">The registered code, for example <c>PLY1002</c>.</param>
/// <param name="Severity">How serious the message is.</param>
/// <param name="Message">The text of the message, without code and position.</param>
/// <param name="File">The file the message is about, or <see langword="null"/> when it has none.</param>
/// <param name="Line">The 1-based line, or 0 when the message has no position.</param>
/// <param name="Column">The 1-based column, or 0 when the message has no position.</param>
public sealed record Diagnostic(string Code, DiagnosticSeverity Severity, string Message, string? File = null, int Line = 0, int Column = 0) {

  /// <summary>The 1-based last line of the range; 0 when the range ends on <see cref="Line"/>.</summary>
  public int EndLine { get; init; }

  /// <summary>The 1-based last column of the range, inclusive; 0 when the range ends at <see cref="Column"/>.</summary>
  public int EndColumn { get; init; }

  /// <summary>
  /// The exit code an error stands for, as its descriptor registers it; <see langword="null"/> for a
  /// warning or an info, and for an error not created from a descriptor, which
  /// <see cref="DiagnosticBag"/> counts as an internal error.
  /// </summary>
  public ExitCode? ExitCode { get; init; }

  /// <summary>The ID of the rule or specification the diagnostic enforces, or <see langword="null"/>.</summary>
  public string? Rule { get; init; }

  /// <summary>Why the input was refused, beyond the message, or <see langword="null"/>.</summary>
  public string? Cause { get; init; }

  /// <summary>What the user can do instead, or <see langword="null"/>.</summary>
  public string? Workaround { get; init; }

  /// <summary>The capability that would make the input acceptable, or <see langword="null"/>.</summary>
  public string? RequiredCapability { get; init; }

  /// <summary>
  /// The invocation as typed (<c>polypiler</c> and every argument, quoted by the rule of ADR 0004),
  /// carried unchanged by the renderers; <see langword="null"/> when no command line is known.
  /// </summary>
  public string? Command { get; init; }

  /// <summary>The source range, or <see langword="null"/> when the diagnostic has no file or no position.</summary>
  public SourceRange? Source => this.File is null || this.Line < 1 || this.Column < 1
    ? null
    : new SourceRange(this.File, this.Line, this.Column, this.EndLine == 0 ? this.Line : this.EndLine, this.EndColumn == 0 ? this.Column : this.EndColumn);

  /// <summary>Creates a diagnostic from its descriptor, the message formatted with the invariant culture.</summary>
  /// <param name="descriptor">The registered code with its severity, exit code, message format and rule.</param>
  /// <param name="source">Where in the input, or <see langword="null"/>.</param>
  /// <param name="arguments">The values of the placeholders of <see cref="DiagnosticDescriptor.MessageFormat"/>.</param>
  /// <returns>The diagnostic.</returns>
  /// <exception cref="FormatException">The arguments do not fit the message format.</exception>
  public static Diagnostic Create(DiagnosticDescriptor descriptor, SourceRange? source, params object[] arguments) {
    ArgumentNullException.ThrowIfNull(descriptor);
    var message = string.Format(CultureInfo.InvariantCulture, descriptor.MessageFormat, arguments);
    return new(descriptor.Code, descriptor.Severity, message, source?.File, source?.StartLine ?? 0, source?.StartColumn ?? 0) {
      EndLine = source?.EndLine ?? 0,
      EndColumn = source?.EndColumn ?? 0,
      ExitCode = descriptor.ExitCode,
      Rule = descriptor.Rule
    };
  }

  /// <summary>Formats the diagnostic as <c>file(line,column): severity CODE: message</c>.</summary>
  public override string ToString() {
    var text = $"{DiagnosticRenderer.SeverityName(this.Severity)} {this.Code}: {this.Message}";
    if (this.File is null)
      return text;

    return this.Line > 0 ? $"{this.File}({this.Line},{this.Column}): {text}" : $"{this.File}: {text}";
  }

}
