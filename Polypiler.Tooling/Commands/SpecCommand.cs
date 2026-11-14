using System;
using System.Collections.Generic;
using System.IO;
using Polypiler.Core;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tooling.Commands;

/// <summary>
/// <c>polypiler-tooling spec validate [--root &lt;dir&gt;] [--family &lt;folder&gt;] [--diagnostics text|json]</c>
/// checks every spec document under the root, <c>spec</c> by default, with <see cref="SpecValidator"/>.
/// Each problem goes to standard error, one line of text or JSON; the last line of standard output is
/// <c>&lt;n&gt; documents, &lt;m&gt; errors</c>. Test paths and family schemas are relative to the
/// repository: the nearest directory above the root that holds <c>.git</c>, else the current directory.
/// </summary>
public static class SpecCommand {

  /// <summary>The verb the command is invoked by.</summary>
  public const string Verb = "spec";

  /// <summary>Runs the arguments that follow the verb in the process's current directory.</summary>
  /// <param name="args">The arguments after <see cref="Verb"/>.</param>
  /// <param name="output">Receives the summary line.</param>
  /// <param name="error">Receives the problems and the refusal of a command line.</param>
  /// <returns>0 without errors, 1 with, 2 for a command line that cannot be understood.</returns>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    => Run(args, output, error, Environment.CurrentDirectory, SpecFamilies.All);

  /// <summary>Runs the arguments that follow the verb.</summary>
  /// <param name="args">The arguments after <see cref="Verb"/>.</param>
  /// <param name="output">Receives the summary line.</param>
  /// <param name="error">Receives the problems and the refusal of a command line.</param>
  /// <param name="currentDirectory">The directory a relative root is resolved against.</param>
  /// <param name="families">The families the validator knows.</param>
  /// <returns>0 without errors, 1 with, 2 for a command line that cannot be understood.</returns>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, string currentDirectory, IEnumerable<ISpecFamily> families) {
    ArgumentNullException.ThrowIfNull(args);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(error);
    if (args.Count == 0 || args[0] != "validate")
      return Usage(error, args.Count == 0 ? "spec needs 'validate'" : $"unknown spec command '{args[0]}'");

    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 1; index < args.Count; ++index) {
      var name = args[index];
      if (name is not ("--root" or "--family" or "--diagnostics"))
        return Usage(error, $"unknown option '{name}'");
      if (index + 1 == args.Count)
        return Usage(error, $"option '{name}' requires a value");
      if (!options.TryAdd(name, args[++index]))
        return Usage(error, $"option '{name}' is given more than once");
    }

    var format = options.GetValueOrDefault("--diagnostics", "text");
    if (format is not ("text" or "json"))
      return Usage(error, $"option '--diagnostics' does not allow '{format}'");

    var root = Path.GetFullPath(Path.Combine(currentDirectory, options.GetValueOrDefault("--root", "spec")));
    var validator = new SpecValidator(families);
    var result = Directory.Exists(root)
      ? validator.Run(SpecTree.Load(root, SpecTree.FindRepositoryRoot(root) ?? currentDirectory), options.GetValueOrDefault("--family"))
      : new SpecValidation(0, [Diagnostic.Create(SpecDiagnostics.Unreadable, null, $"spec root '{options.GetValueOrDefault("--root", "spec")}' does not exist")]);

    foreach (var diagnostic in result.Diagnostics)
      error.WriteLine(format == "json" ? DiagnosticRenderer.ToJson(diagnostic) : DiagnosticRenderer.ToText(diagnostic));

    output.WriteLine($"{result.Documents} documents, {result.Diagnostics.Count} errors");
    return result.Diagnostics.Count == 0 ? Program.Success : Program.Failure;
  }

  private static int Usage(TextWriter error, string message) {
    error.WriteLine($"{Program.Name}: error {ToolchainsCommand.UsageCode}: {message}");
    return Program.UsageError;
  }

}
