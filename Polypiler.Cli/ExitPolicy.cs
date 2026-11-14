using System;
using Polypiler.Core;

namespace Polypiler.Cli;

/// <summary>
/// The single way out of a command that ran (PRD §10.1, ADR 0003, ADR 0004 "Output channels"): the
/// exit code is the one <see cref="DiagnosticBag.ExitCode"/> derives from what the command reported,
/// an exception becomes the internal compiler error <c>PLY0001</c>, and in text format standard
/// error ends with the tally line.
/// </summary>
public static class ExitPolicy {

  /// <summary>The exit code of a run the user cancelled: the shell's code for termination by SIGINT.</summary>
  public const int Cancelled = 130;

  /// <summary>The environment variable that, set to <c>1</c>, adds the stack trace of an internal compiler error to a text standard error.</summary>
  public const string DebugVariable = "POLYPILER_DEBUG";

  /// <summary>The workaround an internal compiler error carries.</summary>
  public const string Workaround = "report this with the command line shown";

  /// <summary>
  /// Ends a command that returned: a non-zero result without a reported error is itself a defect and
  /// is reported as <c>PLY0001</c>; then the tally line is written and the exit code of the
  /// diagnostics returned, whatever number the command returned.
  /// </summary>
  /// <param name="context">The context the command ran with.</param>
  /// <param name="command">The name of the command.</param>
  /// <param name="commandResult">What the command returned.</param>
  /// <returns>The process exit code.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="command"/> is <see langword="null"/>.</exception>
  public static int Complete(CliContext context, string command, int commandResult) {
    ArgumentNullException.ThrowIfNull(context);
    ArgumentNullException.ThrowIfNull(command);
    if (commandResult != 0 && context.Diagnostics.ErrorCount == 0)
      context.Report(Diagnostic.Create(DiagnosticDescriptor.InternalError, null, "") with {
        Message = $"command '{command}' returned {commandResult} without a diagnostic",
        Workaround = Workaround
      });

    return Finish(context);
  }

  /// <summary>
  /// Ends a command that threw, with the value of <see cref="DebugVariable"/> as the process has it.
  /// </summary>
  /// <param name="context">The context the command ran with.</param>
  /// <param name="exception">What the command threw.</param>
  /// <returns><see cref="Cancelled"/> for a cancellation, otherwise the exit code of the diagnostics, which <c>PLY0001</c> makes 70.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static int Fail(CliContext context, Exception exception)
    => Fail(context, exception, Environment.GetEnvironmentVariable(DebugVariable) == "1");

  /// <summary>
  /// Ends a command that threw. An <see cref="OperationCanceledException"/> is the user's
  /// cancellation: no diagnostic, exit code <see cref="Cancelled"/>. Any other exception is reported as
  /// <c>PLY0001</c>, <c>internal compiler error: &lt;type&gt;: &lt;message&gt;</c>, with the stack trace
  /// as its cause, which only the JSON format shows; in text format the stack trace follows the
  /// diagnostic only when <paramref name="debug"/> is set. Then the tally line is written.
  /// </summary>
  /// <param name="context">The context the command ran with.</param>
  /// <param name="exception">What the command threw.</param>
  /// <param name="debug">Whether a text standard error gets the stack trace.</param>
  /// <returns><see cref="Cancelled"/> for a cancellation, otherwise the exit code of the diagnostics, which <c>PLY0001</c> makes 70.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static int Fail(CliContext context, Exception exception, bool debug) {
    ArgumentNullException.ThrowIfNull(context);
    ArgumentNullException.ThrowIfNull(exception);
    if (exception is OperationCanceledException)
      return Cancelled;

    var stackTrace = exception.StackTrace ?? "";
    context.Report(Diagnostic.Create(DiagnosticDescriptor.InternalError, null, $"{exception.GetType().Name}: {exception.Message}") with {
      Cause = stackTrace.Length == 0 ? null : stackTrace,
      Workaround = Workaround
    });
    if (debug && context.Format == DiagnosticsFormat.Text)
      foreach (var line in stackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        context.Error.WriteLine(line.TrimEnd('\r'));

    return Finish(context);
  }

  /// <summary>The tally line: <c>polypiler: &lt;e&gt; error(s), &lt;w&gt; warning(s)</c>.</summary>
  /// <param name="diagnostics">The diagnostics of the run.</param>
  /// <returns>The line.</returns>
  public static string Tally(DiagnosticBag diagnostics) {
    ArgumentNullException.ThrowIfNull(diagnostics);
    return $"{ProductInfo.Name}: {diagnostics.ErrorCount} error(s), {diagnostics.WarningCount} warning(s)";
  }

  /// <summary>Writes the tally line where ADR 0004 asks for it — text format, a diagnostic reported, no <c>--quiet</c> — and returns the exit code.</summary>
  private static int Finish(CliContext context) {
    if (context.Format == DiagnosticsFormat.Text && !context.Quiet && context.Diagnostics.Items.Count > 0)
      context.Error.WriteLine(Tally(context.Diagnostics));

    return (int)context.Diagnostics.ExitCode;
  }

}
