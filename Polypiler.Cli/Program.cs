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

  /// <summary>
  /// The help topics: what <c>polypiler help &lt;topic&gt;</c> runs besides the help of a command. A
  /// topic is no command; it runs only as the word after <c>help</c>.
  /// </summary>
  public static CommandRegistry CreateHelpTopics() {
    var topics = new CommandRegistry();
    topics.Add(new HelpExitCodesCommand());
    return topics;
  }

  /// <summary>Runs the command line against the given writers, in the current directory.</summary>
  public static int Run(string[] args, TextWriter output, TextWriter error)
    => Run(args, output, error, CreateRegistry(), Directory.GetCurrentDirectory());

  /// <summary>
  /// Runs a command line against the given writers with the commands of a registry, relative paths
  /// resolved against a directory. Help and <c>--version</c> are answered first; a command line that
  /// breaks the grammar is refused with exactly one diagnostic, exit code 2, an empty standard output
  /// and nothing executed; any other runs its command, which leaves through <see cref="ExitPolicy"/>:
  /// what it returns through <see cref="ExitPolicy.Complete"/>, what it throws through
  /// <see cref="ExitPolicy.Fail(CliContext, Exception)"/>. An exception outside a command is an
  /// internal compiler error too.
  /// </summary>
  /// <param name="args">The arguments, without the program name.</param>
  /// <param name="output">Standard output.</param>
  /// <param name="error">Standard error.</param>
  /// <param name="registry">The commands.</param>
  /// <param name="workingDirectory">The directory relative paths are resolved against.</param>
  /// <returns>
  /// The exit code: the one the diagnostics give (ADR 0003); 70 for an exception or for a command that
  /// failed without reporting an error, 130 for a cancellation.
  /// </returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, CommandRegistry registry, string workingDirectory) {
    ArgumentNullException.ThrowIfNull(args);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(error);
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(workingDirectory);

    var commandLine = CliContext.Invocation(args);
    var format = CommandLineParser.RequestedFormat(args);
    try {
      return Dispatch(args, output, error, registry, workingDirectory, commandLine, format);
    } catch (Exception exception) {
      return ExitPolicy.Fail(new CliContext(output, error, commandLine, format, false, workingDirectory), exception);
    }
  }

  private static int Dispatch(IReadOnlyList<string> args, TextWriter output, TextWriter error, CommandRegistry registry, string workingDirectory, string commandLine, DiagnosticsFormat format) {
    if (args is ["--version"]) {
      output.WriteLine(ProductInfo.Version);
      return (int)ExitCode.Success;
    }

    if (args is ["help", _, var surplus, ..])
      return Refuse(CommandLineParser.Grammar(CliDiagnostics.UnexpectedArgument, surplus));

    if (args is ["help", var topic] && CreateHelpTopics() is var topics && topics.Find(topic) is not null)
      return Execute(new CommandLineParser(topics).Parse([topic], out _)!);

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
    return parsed is null ? Refuse(refusal!) : Execute(parsed);

    int Execute(ParsedCommandLine parsed) {
      var context = new CliContext(output, error, commandLine, parsed.Format, parsed.Quiet, workingDirectory);
      try {
        return ExitPolicy.Complete(context, parsed.Command.Name, parsed.Command.Execute(parsed.Parse, context));
      } catch (Exception exception) {
        return ExitPolicy.Fail(context, exception);
      }
    }

    int Refuse(Diagnostic diagnostic) {
      var refused = new CliContext(output, error, commandLine, format, false, workingDirectory);
      refused.Report(diagnostic);
      return (int)refused.Diagnostics.ExitCode;
    }
  }

}
