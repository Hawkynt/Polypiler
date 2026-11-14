using System.CommandLine;

namespace Polypiler.Cli;

/// <summary>
/// A command of <c>polypiler</c>, such as <c>translate</c>. A command is added by registering one
/// instance in <see cref="Program.CreateRegistry"/>; the foundation parses its command line, refuses
/// what breaks the grammar of ADR 0004, prints its help, and calls <see cref="Execute"/> only for a
/// command line it accepted.
/// </summary>
public interface ICliCommand {

  /// <summary>The command name: lower-case words joined by single hyphens, never <c>help</c>.</summary>
  string Name { get; }

  /// <summary>One line that says what the command does, shown in the root help.</summary>
  string Description { get; }

  /// <summary>
  /// Declares the operands and options of the command on <paramref name="command"/>. An
  /// <see cref="Option{T}"/> of <see cref="bool"/> is a flag, one of <see cref="T:string[]"/> a comma
  /// list that may be repeated, any other a single value; <see cref="OptionRules"/> adds the allowed
  /// values of an option and the pairs of options that exclude each other.
  /// </summary>
  /// <param name="command">The command to add the symbols to.</param>
  void Configure(Command command);

  /// <summary>Runs the command on a command line the foundation accepted.</summary>
  /// <param name="parse">The parsed command line, for the values of the symbols <see cref="Configure"/> declared.</param>
  /// <param name="context">The streams, the diagnostics and the global options of the run.</param>
  /// <returns>An exit code, or 0 to let <see cref="CliContext.Diagnostics"/> decide it.</returns>
  int Execute(ParseResult parse, CliContext context);

}
