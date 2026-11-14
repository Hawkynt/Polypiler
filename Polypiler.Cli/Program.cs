using System;
using System.Collections.Generic;
using System.IO;
using Polypiler.Core;

namespace Polypiler.Cli;

/// <summary>
/// Entry point of the <c>polypiler</c> command: the grammar of ADR 0004 over the commands of
/// <see cref="CreateRegistry"/>. <c>polypiler [--diagnostics text|json] [--quiet] &lt;command&gt; [arguments] [options]</c>.
/// </summary>
public static class Program {

  /// <summary>Runs the command line and returns the process exit code.</summary>
  public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

  /// <summary>The commands of <c>polypiler</c>: the single place a command is registered.</summary>
  public static CommandRegistry CreateRegistry() {
    var registry = new CommandRegistry();
    registry.Add(new TranslateCommand());
    return registry;
  }

  /// <summary>Runs the command line against the given writers, in the current directory.</summary>
  public static int Run(string[] args, TextWriter output, TextWriter error)
    => Run(args, output, error, CreateRegistry(), Directory.GetCurrentDirectory());

  /// <summary>
  /// Runs a command line against the given writers with the commands of a registry, relative paths
  /// resolved against a directory. Help and <c>--version</c> are answered first; a command line that
  /// breaks the grammar is refused with exactly one diagnostic, exit code 2, an empty standard output
  /// and nothing executed; any other runs its command.
  /// </summary>
  /// <param name="args">The arguments, without the program name.</param>
  /// <param name="output">Standard output.</param>
  /// <param name="error">Standard error.</param>
  /// <param name="registry">The commands.</param>
  /// <param name="workingDirectory">The directory relative paths are resolved against.</param>
  /// <returns>
  /// The exit code: the one the command returns when it is not 0, otherwise the one its diagnostics
  /// give (ADR 0003).
  /// </returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, CommandRegistry registry, string workingDirectory) {
    ArgumentNullException.ThrowIfNull(args);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(error);
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(workingDirectory);

    if (args is ["--version"]) {
      output.WriteLine(ProductInfo.Version);
      return (int)ExitCode.Success;
    }

    var commandLine = CliContext.Invocation(args);
    var format = CommandLineParser.RequestedFormat(args);
    if (args is ["help", _, var surplus, ..])
      return Refuse(CommandLineParser.Grammar(CliDiagnostics.UnexpectedArgument, surplus));

    IReadOnlyList<string> line = args is ["help", var word] ? [word, "--help"] : args;
    if (line.Count == 0 || line is ["help"]) {
      CliHelp.Root(output, registry);
      return (int)ExitCode.Success;
    }

    if (CommandLineParser.AsksForHelp(line)) {
      var name = CommandLineParser.CommandWord(line);
      if (name is null) {
        CliHelp.Root(output, registry);
        return (int)ExitCode.Success;
      }

      if (registry.Find(name) is not { } wanted)
        return Refuse(Diagnostic.Create(CliDiagnostics.UnknownCommand, null, name));

      CliHelp.Command(output, wanted);
      return (int)ExitCode.Success;
    }

    var parsed = new CommandLineParser(registry).Parse(args, out var refusal);
    if (parsed is null)
      return Refuse(refusal!);

    var context = new CliContext(output, error, commandLine, parsed.Format, parsed.Quiet, workingDirectory);
    var exitCode = parsed.Command.Execute(parsed.Parse, context);
    return exitCode != 0 ? exitCode : (int)context.Diagnostics.ExitCode;

    int Refuse(Diagnostic diagnostic) {
      var refused = new CliContext(output, error, commandLine, format, false, workingDirectory);
      refused.Report(diagnostic);
      return (int)refused.Diagnostics.ExitCode;
    }
  }

}
