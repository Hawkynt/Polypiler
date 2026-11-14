using System;
using System.Text.RegularExpressions;

namespace Polypiler.Core;

/// <summary>
/// A registered diagnostic: its code with the severity, exit code, message format and rule that
/// <c>spec/diagnostics.yaml</c> records for it. Every descriptor is declared as a
/// <see langword="static"/> <see langword="readonly"/> field marked <see cref="DiagnosticAttribute"/>,
/// where the registry test finds it.
/// </summary>
public sealed partial record DiagnosticDescriptor {

  /// <summary>An unexpected exception: the internal compiler error, whose presence makes the exit code <see cref="Core.ExitCode.InternalError"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor InternalError = new("PLY0001", DiagnosticSeverity.Error, Core.ExitCode.InternalError, "internal compiler error: {0}", "adr-0003.internal-error");

  /// <summary>Creates a descriptor.</summary>
  /// <param name="code">The code, <c>PLY</c> and four digits.</param>
  /// <param name="severity">How serious the diagnostic is.</param>
  /// <param name="exitCode">The exit code; given exactly for an error.</param>
  /// <param name="messageFormat">The message, with composite-format placeholders for the arguments.</param>
  /// <param name="rule">The ID of the rule or specification the diagnostic enforces.</param>
  /// <exception cref="ArgumentNullException">A text is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException">The code is malformed, or the exit code is given for a non-error or missing for an error.</exception>
  public DiagnosticDescriptor(string code, DiagnosticSeverity severity, ExitCode? exitCode, string messageFormat, string rule) {
    ArgumentNullException.ThrowIfNull(code);
    ArgumentNullException.ThrowIfNull(messageFormat);
    ArgumentNullException.ThrowIfNull(rule);
    if (!IsCode(code))
      throw new ArgumentException($"'{code}' is not a diagnostic code", nameof(code));
    if ((severity == DiagnosticSeverity.Error) != exitCode.HasValue)
      throw new ArgumentException(exitCode.HasValue ? $"{code}: only errors carry an exit code" : $"{code}: an error needs an exit code", nameof(exitCode));

    this.Code = code;
    this.Severity = severity;
    this.ExitCode = exitCode;
    this.MessageFormat = messageFormat;
    this.Rule = rule;
  }

  /// <summary>The code, <c>PLY</c> and four digits.</summary>
  public string Code { get; }

  /// <summary>How serious the diagnostic is.</summary>
  public DiagnosticSeverity Severity { get; }

  /// <summary>The exit code an error stands for; <see langword="null"/> for a warning or an info.</summary>
  public ExitCode? ExitCode { get; }

  /// <summary>The message, with composite-format placeholders for the arguments.</summary>
  public string MessageFormat { get; }

  /// <summary>The ID of the rule or specification the diagnostic enforces.</summary>
  public string Rule { get; }

  /// <summary>
  /// A diagnostic of this descriptor whose message the emitter has written itself, at a position
  /// given as line and column: it carries the code, severity, exit code and rule of the descriptor,
  /// as one made by <see cref="Diagnostic.Create"/> does.
  /// </summary>
  /// <param name="message">The message, without code and position.</param>
  /// <param name="file">The file the message is about, or <see langword="null"/>.</param>
  /// <param name="line">The 1-based line, or 0 for none.</param>
  /// <param name="column">The 1-based column, or 0 for none.</param>
  /// <returns>The diagnostic.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
  public Diagnostic At(string message, string? file = null, int line = 0, int column = 0) {
    ArgumentNullException.ThrowIfNull(message);
    return new(this.Code, this.Severity, message, file, line, column) { ExitCode = this.ExitCode, Rule = this.Rule };
  }

  /// <summary>Whether a text is a diagnostic code: <c>PLY</c> and exactly four ASCII digits.</summary>
  public static bool IsCode(string? text) => text is not null && CodePattern().IsMatch(text);

  [GeneratedRegex("^PLY[0-9]{4}$")]
  private static partial Regex CodePattern();

}

/// <summary>
/// Marks a <see langword="static"/> <see langword="readonly"/> <see cref="DiagnosticDescriptor"/>
/// field as a declared diagnostic, so that the registry test compares it with
/// <c>spec/diagnostics.yaml</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class DiagnosticAttribute : Attribute;
