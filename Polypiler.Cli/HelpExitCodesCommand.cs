using System;
using System.CommandLine;

namespace Polypiler.Cli;

/// <summary>
/// <c>polypiler help exit-codes</c>: one line per row of <see cref="ExitCodeTable"/>,
/// <c>&lt;code, right-aligned to 3&gt;  &lt;meaning&gt;</c>. It is a help topic, registered in
/// <see cref="Program.CreateHelpTopics"/> rather than as a command, so <c>polypiler exit-codes</c> is
/// no command line.
/// </summary>
public sealed class HelpExitCodesCommand : ICliCommand {

  /// <inheritdoc/>
  public string Name => "exit-codes";

  /// <inheritdoc/>
  public string Description => "the exit codes of polypiler and what each means";

  /// <inheritdoc/>
  public void Configure(Command command) { }

  /// <inheritdoc/>
  public int Execute(ParseResult parse, CliContext context) {
    ArgumentNullException.ThrowIfNull(context);
    foreach (var line in ExitCodeTable.HelpLines())
      context.Output.WriteLine(line);

    return 0;
  }

}
