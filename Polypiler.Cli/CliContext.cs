using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Polypiler.Core;

namespace Polypiler.Cli;

/// <summary>The format of the diagnostics on standard error, as <c>--diagnostics</c> selects it.</summary>
public enum DiagnosticsFormat {

  /// <summary>One line of text per diagnostic (the default).</summary>
  Text,

  /// <summary>One JSON object per line, and nothing else on standard error.</summary>
  Json

}

/// <summary>
/// What a command runs with: the two streams, the diagnostics of the run, the invocation as typed and
/// the two global options. A diagnostic is reported through <see cref="Report"/>, which keeps it in
/// <see cref="Diagnostics"/> and writes it to <see cref="Error"/> in the selected format.
/// </summary>
public sealed class CliContext : IDiagnosticSink {

  /// <summary>Creates a context.</summary>
  /// <param name="output">Standard output: the result of the command and nothing else.</param>
  /// <param name="error">Standard error: the diagnostics.</param>
  /// <param name="commandLine">The invocation as typed, as <see cref="Invocation"/> writes it; empty when none is known.</param>
  /// <param name="format">The format of the diagnostics.</param>
  /// <param name="quiet">Whether infos and warnings are left off standard error.</param>
  /// <param name="workingDirectory">The directory relative paths of the command line are resolved against.</param>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public CliContext(TextWriter output, TextWriter error, string commandLine, DiagnosticsFormat format, bool quiet, string workingDirectory) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(error);
    ArgumentNullException.ThrowIfNull(commandLine);
    ArgumentNullException.ThrowIfNull(workingDirectory);
    this.Output = output;
    this.Error = error;
    this.CommandLine = commandLine;
    this.Format = format;
    this.Quiet = quiet;
    this.WorkingDirectory = workingDirectory;
  }

  /// <summary>Standard output: the result of the command and nothing else.</summary>
  public TextWriter Output { get; }

  /// <summary>Standard error: the diagnostics, in <see cref="Format"/>.</summary>
  public TextWriter Error { get; }

  /// <summary>Every diagnostic reported, and the exit code they give.</summary>
  public DiagnosticBag Diagnostics { get; } = new();

  /// <summary>
  /// The invocation as typed: <c>polypiler</c> and every argument in the order given, quoted by the
  /// rule of ADR 0004 and not normalised; the <see cref="Diagnostic.Command"/> of every diagnostic.
  /// </summary>
  public string CommandLine { get; }

  /// <summary>The format of the diagnostics on standard error.</summary>
  public DiagnosticsFormat Format { get; }

  /// <summary>Whether infos and warnings are left off standard error; it never changes standard output or the exit code.</summary>
  public bool Quiet { get; }

  /// <summary>The directory relative paths of the command line are resolved against.</summary>
  public string WorkingDirectory { get; }

  /// <summary>
  /// Keeps a diagnostic, with <see cref="CommandLine"/> as its command, and writes it to
  /// <see cref="Error"/> in <see cref="Format"/>; with <see cref="Quiet"/> only an error is written.
  /// </summary>
  /// <param name="diagnostic">The diagnostic.</param>
  /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is <see langword="null"/>.</exception>
  public void Report(Diagnostic diagnostic) {
    ArgumentNullException.ThrowIfNull(diagnostic);
    if (this.CommandLine.Length > 0)
      diagnostic = diagnostic with { Command = this.CommandLine };

    this.Diagnostics.Report(diagnostic);
    if (this.Quiet && diagnostic.Severity != DiagnosticSeverity.Error)
      return;

    this.Error.WriteLine(this.Format == DiagnosticsFormat.Json ? DiagnosticRenderer.ToJson(diagnostic) : DiagnosticRenderer.ToText(diagnostic));
  }

  /// <summary>
  /// Writes a line of text to standard error that is no diagnostic, such as an explanation a command
  /// was asked for: only in text format, which keeps a JSON standard error machine-readable, and not
  /// with <see cref="Quiet"/>.
  /// </summary>
  /// <param name="line">The line, without a line break.</param>
  public void Note(string line) {
    if (this.Format == DiagnosticsFormat.Text && !this.Quiet)
      this.Error.WriteLine(line);
  }

  /// <summary>The full path of a path of the command line, relative to <see cref="WorkingDirectory"/>.</summary>
  /// <param name="path">The path as typed.</param>
  /// <returns>The full path.</returns>
  /// <exception cref="ArgumentException">The path contains a character no path may contain.</exception>
  public string Resolve(string path) => Path.GetFullPath(Path.Combine(this.WorkingDirectory, path));

  /// <summary>
  /// The invocation as typed (ADR 0004, "Recorded command line"): <c>polypiler</c> and every argument
  /// in the order given. An argument that is not empty and consists only of ASCII letters, digits and
  /// <c>_ . / : = , + @ % -</c> is written bare; any other in double quotes, with <c>\</c>, <c>"</c>,
  /// <c>$</c> and <c>`</c> preceded by a backslash.
  /// </summary>
  /// <param name="arguments">The arguments, without the program name.</param>
  /// <returns>The line.</returns>
  public static string Invocation(IEnumerable<string> arguments)
    => string.Join(' ', arguments.Select(Quote).Prepend(ProductInfo.Name));

  private static string Quote(string argument) {
    if (argument.Length > 0 && argument.All(IsBare))
      return argument;

    var quoted = new StringBuilder("\"");
    foreach (var character in argument) {
      if (character is '\\' or '"' or '$' or '`')
        quoted.Append('\\');

      quoted.Append(character);
    }

    return quoted.Append('"').ToString();
  }

  private static bool IsBare(char character) => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '/' or ':' or '=' or ',' or '+' or '@' or '%' or '-';

}
