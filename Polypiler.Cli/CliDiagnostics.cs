using Polypiler.Core;

namespace Polypiler.Cli;

/// <summary>
/// The diagnostics of the command-line foundation (ADR 0004): the refusals of the grammar and the
/// checks of input and output paths, with the message texts each of them takes.
/// </summary>
public static class CliDiagnostics {

  /// <summary><c>PLY0002</c>: the command name is no registered command.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnknownCommand = new("PLY0002", DiagnosticSeverity.Error, ExitCode.UsageError, "unknown command '{0}'; run 'polypiler --help'", "adr-0004.grammar");

  /// <summary><c>PLY0003</c>: the command line breaks the grammar or names what the command does not take; the message is one of the texts of this class.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor CommandLine = new("PLY0003", DiagnosticSeverity.Error, ExitCode.UsageError, "{0}", "adr-0004.grammar");

  /// <summary><c>PLY0004</c>: a file cannot be read; the message is <see cref="InputUnreadable"/> for an input of the command line.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unreadable = new("PLY0004", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "io.read");

  /// <summary><c>PLY0005</c>: the output cannot be written; the message is <see cref="OutputNotEmpty"/> or <see cref="OutputNotCreatable"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unwritable = new("PLY0005", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "io.write");

  /// <summary><c>PLY0011</c>: two options the command declares exclusive were both given.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ExclusiveOptions = new("PLY0011", DiagnosticSeverity.Error, ExitCode.UsageError, "options '{0}' and '{1}' cannot be combined", "adr-0004.grammar");

  /// <summary>The option name is not one the command line takes at its place.</summary>
  public const string UnknownOption = "unknown option '{0}'";

  /// <summary>An option that takes a value stands without one.</summary>
  public const string RequiresValue = "option '{0}' requires a value";

  /// <summary>A flag is given a value.</summary>
  public const string TakesNoValue = "option '{0}' does not take a value";

  /// <summary>An option that is not multi-valued appears again.</summary>
  public const string GivenTwice = "option '{0}' given more than once";

  /// <summary>A value outside the set the option allows: the option, the value and the allowed values.</summary>
  public const string NotAllowed = "'{1}' is not a valid value for '{0}'; expected one of: {2}";

  /// <summary>A list option has an empty item.</summary>
  public const string EmptyListElement = "option '{0}' has an empty list element";

  /// <summary>A required option is absent.</summary>
  public const string Required = "option '{0}' is required";

  /// <summary>An operand is missing.</summary>
  public const string MissingArgument = "missing argument <{0}>";

  /// <summary>An operand more than the command takes.</summary>
  public const string UnexpectedArgument = "unexpected argument '{0}'";

  /// <summary>The command line names no command.</summary>
  public const string NoCommand = "no command given";

  /// <summary>An input of the command line does not exist or cannot be opened for reading.</summary>
  public const string InputUnreadable = "input '{0}' does not exist or is not readable";

  /// <summary>An output directory has files and <c>--force</c> is absent.</summary>
  public const string OutputNotEmpty = "output directory '{0}' is not empty; pass --force to write into it";

  /// <summary>An output path cannot be created or written: the path and the reason.</summary>
  public const string OutputNotCreatable = "output path '{0}' cannot be created: {1}";

}
