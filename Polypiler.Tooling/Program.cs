using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Report;

namespace Polypiler.Tooling;

/// <summary>Entry point of the <c>polypiler-tooling</c> command, the repository's maintenance tool.</summary>
public static class Program {

  /// <summary>The name the command is invoked by.</summary>
  public const string Name = "polypiler-tooling";

  /// <summary>Exit code for a successful run.</summary>
  public const int Success = 0;

  /// <summary>Exit code for a verb that ran and failed.</summary>
  public const int Failure = 1;

  /// <summary>Exit code for a command line that could not be understood.</summary>
  public const int UsageError = 2;

  /// <summary>
  /// The verb that imports the portable floating-point implementations and writes them as an IR
  /// module in bytes; the build embeds the file into the <c>polypiler</c> executable.
  /// </summary>
  public const string FloatImplementations = "float-implementations";

  /// <summary>Runs the command line and returns the process exit code.</summary>
  public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

  /// <summary>Runs the command line against the given writers.</summary>
  public static int Run(string[] args, TextWriter output, TextWriter error) {
    if (args.Length == 0) {
      output.WriteLine($"usage: {Name} <verb> [options]");
      output.WriteLine($"       {Name} {FloatImplementations} <file>");
      output.WriteLine($"       {Name} {ToolchainsCommand.Verb} validate [--lock <path>]");
      output.WriteLine($"       {Name} {ToolchainsCommand.Verb} probe [--lock <path>] [--only <key>[,<key>...]] [--format text|json]");
      output.WriteLine($"       {Name} {DiagnosticsCommand.Verb} validate [--registry <path>]");
      output.WriteLine($"       {Name} {ErrorCatalogueCommand.Verb} {ErrorCatalogueCommand.Report} [--registry <file>] [--notes <dir>] [--out <dir>] [--check]");
      return Success;
    }

    if (args[0] == FloatImplementations) {
      if (args.Length == 2)
        return WriteFloatImplementations([.. FloatOperationSources.All.Select(source => new CSharpSource(source.Name, source.Text))], args[1], output, error);

      error.WriteLine($"{Name}: '{FloatImplementations}' takes one argument, the file to write.");
      return UsageError;
    }

    if (args[0] == ToolchainsCommand.Verb)
      return new ToolchainsCommand().Run(args[1..], output, error);

    if (args[0] == DiagnosticsCommand.Verb)
      return DiagnosticsCommand.Run(args[1..], output, error);

    if (args[0] == ErrorCatalogueCommand.Verb)
      return ErrorCatalogueCommand.Run(args[1..], output, error);

    error.WriteLine($"{Name}: unknown verb '{args[0]}'.");
    return UsageError;
  }

  /// <summary>
  /// Imports C# sources with the C# frontend and writes the module as an <see cref="IrModuleFormat"/>
  /// file; <see cref="FloatImplementations"/> does this with <see cref="FloatOperationSources"/>.
  /// </summary>
  /// <param name="sources">The files of the module.</param>
  /// <param name="file">The file to write.</param>
  /// <param name="output">Receives the name of the file written.</param>
  /// <param name="error">Receives the diagnostics of the import and what keeps the file from being written.</param>
  /// <returns><see cref="Success"/>, or <see cref="Failure"/> when the sources do not import or the file cannot be written.</returns>
  public static int WriteFloatImplementations(IReadOnlyList<CSharpSource> sources, string file, TextWriter output, TextWriter error) {
    var imported = CSharpFrontend.Import(sources);
    foreach (var diagnostic in imported.Diagnostics)
      error.WriteLine(diagnostic);

    if (imported.Module is null)
      return Failure;

    try {
      File.WriteAllBytes(file, IrModuleFormat.Write(imported.Module));
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      error.WriteLine($"{Name}: '{file}' cannot be written: {exception.Message}");
      return Failure;
    }

    output.WriteLine(file);
    return Success;
  }

}
