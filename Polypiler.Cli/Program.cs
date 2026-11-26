using System;
using System.Collections.Generic;
using System.IO;
using Polypiler.Cli.Options;
using Polypiler.Cli.Verify;
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
    registry.Add(new TargetsCommand());
    registry.Add(new TranslateCommand());
    registry.Add(new VerifyCommand());
    return registry;
  }

  /// <summary>
  /// The options and identifiers the areas contribute to the commands of <see cref="CreateRegistry"/>:
  /// the single place a contributor is registered, next to the commands.
  /// </summary>
  public static OptionContributions CreateContributions() => CreateContributions(CreateRegistry());

  /// <summary>The contributions of <see cref="CreateContributions()"/>, checked against the commands of a registry.</summary>
  /// <param name="registry">The commands.</param>
  /// <returns>The contributions.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="registry"/> is <see langword="null"/>.</exception>
  /// <exception cref="InvalidOperationException">A contributor breaks a naming, collision or declaration rule.</exception>
  public static OptionContributions CreateContributions(CommandRegistry registry) => new(registry);

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
  public static int Run(string[] args, TextWriter output, TextWriter error) {
    var registry = CreateRegistry();
    return Run(args, output, error, registry, CreateContributions(registry), Directory.GetCurrentDirectory());
  }

  /// <summary>Runs a command line with the commands of a registry and no contributed option; see <see cref="Run(IReadOnlyList{string}, TextWriter, TextWriter, CommandRegistry, OptionContributions, string)"/>.</summary>
  /// <param name="args">The arguments, without the program name.</param>
  /// <param name="output">Standard output.</param>
  /// <param name="error">Standard error.</param>
  /// <param name="registry">The commands.</param>
  /// <param name="workingDirectory">The directory relative paths are resolved against.</param>
  /// <returns>The exit code.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, CommandRegistry registry, string workingDirectory) {
    ArgumentNullException.ThrowIfNull(registry);
    return Run(args, output, error, registry, new OptionContributions(registry), workingDirectory);
  }

  /// <summary>
  /// Runs a command line against the given writers with the commands of a registry and the options
  /// contributed to them, relative paths resolved against a directory. Help and <c>--version</c> are answered first; a command line that
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
  /// <param name="contributions">The options and identifiers contributed to the commands.</param>
  /// <param name="workingDirectory">The directory relative paths are resolved against.</param>
  /// <returns>
  /// The exit code: the one the diagnostics give (ADR 0003); 70 for an exception or for a command that
  /// failed without reporting an error, 130 for a cancellation.
  /// </returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, CommandRegistry registry, OptionContributions contributions, string workingDirectory) {
    ArgumentNullException.ThrowIfNull(args);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(error);
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(contributions);
    ArgumentNullException.ThrowIfNull(workingDirectory);

    var commandLine = CliContext.Invocation(args);
    var format = CommandLineParser.RequestedFormat(args);
    try {
      return Dispatch(args, output, error, registry, contributions, workingDirectory, commandLine, format);
    } catch (Exception exception) {
      return ExitPolicy.Fail(new CliContext(output, error, commandLine, format, false, workingDirectory), exception);
    }
  }

  private static int Dispatch(IReadOnlyList<string> args, TextWriter output, TextWriter error, CommandRegistry registry, OptionContributions contributions, string workingDirectory, string commandLine, DiagnosticsFormat format) {
    if (args is ["--version"]) {
      output.WriteLine(ProductInfo.Version);
      return (int)ExitCode.Success;
    }

    if (args is ["help", _, var surplus, ..])
      return Refuse(CommandLineParser.Grammar(CliDiagnostics.UnexpectedArgument, surplus));

    if (args is ["help", var topic] && CreateHelpTopics() is var topics && topics.Find(topic) is not null)
      return Execute(new CommandLineParser(topics, contributions).Parse([topic], out _)!);

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

      CliHelp.Command(output, wanted, contributions);
      return (int)ExitCode.Success;
    }

    var parsed = new CommandLineParser(registry, contributions).Parse(args, out var refusal);
    return parsed is null ? Refuse(refusal!) : Execute(parsed);

    // A contributor that refused the command line has reported why; the command does not run.
    int Execute(ParsedCommandLine parsed) {
      var context = new CliContext(output, error, commandLine, parsed.Format, parsed.Quiet, workingDirectory);
      try {
        var name = parsed.Command.Name;
        if (contributions.ReadFor(name, parsed.Parse, context) is not { } bag)
          return ExitPolicy.Complete(context, name, (int)ExitCode.UsageError);

        context.Contributed = bag;
        context.ContributedArguments = contributions.CanonicalArgumentsFor(name, bag);
        return ExitPolicy.Complete(context, name, parsed.Command.Execute(parsed.Parse, context));
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
