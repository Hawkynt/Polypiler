using System.Collections.Generic;
using System.CommandLine;

namespace Polypiler.Cli.Options;

/// <summary>
/// An area's options on commands it does not own (ADR 0004, "Adding a command or an option later"):
/// registered once in <see cref="Program.CreateContributions(CommandRegistry)"/>, configured onto
/// every command it names, read before that command runs and recorded in its canonical command line.
/// </summary>
public interface ICommandOptionContributor {

  /// <summary>The owner: the backlog ID or issue of the area that defines the options, the heading of their group in the help.</summary>
  string Owner { get; }

  /// <summary>The commands the options are added to, by name.</summary>
  IReadOnlyList<string> Commands { get; }

  /// <summary>One declaration per option <see cref="Configure"/> adds.</summary>
  IReadOnlyList<ContributedOption> Options { get; }

  /// <summary>
  /// Adds the options to one of <see cref="Commands"/>, exactly those <see cref="Options"/> declares:
  /// an <see cref="Option{T}"/> of <see cref="bool"/> for a flag, of <see cref="T:string[]"/> for a list
  /// or a repeat-only option, any other for a single value.
  /// </summary>
  /// <param name="commandName">The name of the command.</param>
  /// <param name="command">The command to add the options to.</param>
  void Configure(string commandName, Command command);

  /// <summary>
  /// Reads the options from a command line the grammar accepted into the bag; a value the area
  /// does not take is reported with the area's own diagnostic.
  /// </summary>
  /// <param name="commandName">The name of the command.</param>
  /// <param name="parse">The parsed command line.</param>
  /// <param name="bag">Receives the values.</param>
  /// <param name="context">The context of the run, to report through.</param>
  /// <returns><see langword="false"/> after reporting an error: the command does not run.</returns>
  bool Read(string commandName, ParseResult parse, OptionBag bag, CliContext context);

  /// <summary>
  /// The options of the bag as words: <c>--name</c> for a flag that is set, <c>--name value</c> for
  /// every value otherwise, an option with a default also when it was not typed.
  /// <see cref="OptionContributions"/> leaves out what is not recorded, spells every value normally,
  /// joins and sorts lists and orders the options by name.
  /// </summary>
  /// <param name="bag">The bag <see cref="Read"/> filled.</param>
  /// <returns>The words.</returns>
  IEnumerable<string> CanonicalArguments(OptionBag bag);

}
