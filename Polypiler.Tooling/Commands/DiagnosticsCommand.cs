using System;
using System.Collections.Generic;
using System.IO;
using Polypiler.Core;
using Polypiler.Tooling.Diagnostics;

namespace Polypiler.Tooling.Commands;

/// <summary>
/// <c>polypiler-tooling diagnostics validate [--registry &lt;path&gt;]</c> checks the diagnostics
/// registry, <c>spec/diagnostics.yaml</c> by default: each problem goes to standard error as a
/// <c>PLY0045</c> line, and standard output ends with <c>&lt;n&gt; codes, &lt;k&gt; problems</c>.
/// </summary>
public static class DiagnosticsCommand {

  /// <summary>The verb the command is invoked by.</summary>
  public const string Verb = "diagnostics";

  /// <summary>Runs the arguments that follow the verb.</summary>
  /// <param name="args">The arguments after <see cref="Verb"/>.</param>
  /// <param name="output">Receives the summary line.</param>
  /// <param name="error">Receives the problems and the refusal of a command line.</param>
  /// <returns>0 for a valid registry, 1 for an invalid one, 2 for a command line that cannot be understood.</returns>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    => Run(args, output, error, Environment.CurrentDirectory);

  /// <summary>Runs the arguments that follow the verb, with the default registry below a directory of the caller's.</summary>
  /// <param name="args">The arguments after <see cref="Verb"/>.</param>
  /// <param name="output">Receives the summary line.</param>
  /// <param name="error">Receives the problems and the refusal of a command line.</param>
  /// <param name="currentDirectory">The directory <see cref="DiagnosticRegistry.RepositoryPath"/> is read below when <c>--registry</c> is not given.</param>
  /// <returns>0 for a valid registry, 1 for an invalid one, 2 for a command line that cannot be understood.</returns>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, string currentDirectory) {
    if (args.Count == 0 || args[0] != "validate")
      return Usage(error, args.Count == 0 ? "diagnostics needs 'validate'" : $"unknown diagnostics command '{args[0]}'");

    string? registry = null;
    for (var index = 1; index < args.Count; ++index) {
      if (args[index] != "--registry")
        return Usage(error, $"unknown option '{args[index]}'");
      if (index + 1 == args.Count)
        return Usage(error, "option '--registry' requires a value");
      if (registry is not null)
        return Usage(error, "option '--registry' is given more than once");
      registry = args[++index];
    }

    var result = DiagnosticRegistryValidator.Validate(registry ?? Path.Combine(currentDirectory, DiagnosticRegistry.RepositoryPath));
    foreach (var problem in result.Problems)
      error.WriteLine(DiagnosticRenderer.ToText(problem));

    output.WriteLine($"{Counted(result.Codes.Count, "code")}, {Counted(result.Problems.Count, "problem")}");
    return result.Problems.Count == 0 ? Program.Success : Program.Failure;
  }

  private static string Counted(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

  private static int Usage(TextWriter error, string message) {
    error.WriteLine($"{Program.Name}: error {ToolchainsCommand.UsageCode}: {message}");
    return Program.UsageError;
  }

}
