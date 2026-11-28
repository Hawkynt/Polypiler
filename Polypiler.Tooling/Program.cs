using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Tooling.Bench;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Fuzz;
using Polypiler.Tooling.Permutations;
using Polypiler.Tooling.Rebuild;
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
      output.WriteLine($"       {Name} {ErrorCatalogueCommand.Verb} {ReportCapabilitiesCommand.Report} [--gates <dir>] [--spec <dir>] [--out <dir>] [--commit <sha>] [--check]");
      output.WriteLine($"       {Name} {ErrorCatalogueCommand.Verb} {ShowcaseCommand.Report} [--examples <dir>] [--out <file>] [--polypiler <path>] [--targets <a,b,...>] [--check]");
      output.WriteLine($"       {Name} {SpecCommand.Verb} validate [--root <dir>] [--family <folder>] [--diagnostics text|json]");
      output.WriteLine($"       {Name} {CorpusCommand.Verb} import [--source <dir>]... [--update-lock] [--fixture <id>]...");
      output.WriteLine($"       {Name} {CorpusCommand.Verb} verify");
      output.WriteLine($"       {Name} {CorpusCommand.Verb} discover --repository <owner/repo> --glob <pattern> [--source <dir>]...");
      output.WriteLine($"       {Name} {CipherCommand.Verb} kat (<file>... | --all) [--targets csharp,rust] [--require <target>,...] [--jobs <n>] [--polypiler <path>] [--report <file.md>] [--work <dir>]");
      output.WriteLine($"       {Name} {FuzzCommand.Verb} run --target <id> [--seed <n>] [--iterations <n>] [--engine inprocess|afl] [--duration <seconds>] [--assembly <path>]...");
      output.WriteLine($"       {Name} {FuzzCommand.Verb} minimize --target <id> --input <file> [--assembly <path>]...");
      output.WriteLine($"       {Name} {FuzzCommand.Verb} replay [--target <id>] [--assembly <path>]...");
      output.WriteLine($"       {Name} {CiCommand.Verb} audit [--root <dir>]");
      output.WriteLine($"       {Name} {CiCommand.Verb} matrix (--changed <file> | --all) [--root <dir>]");
      output.WriteLine($"       {Name} {CiCommand.Verb} env --layer <name> [--root <dir>]");
      output.WriteLine($"       {Name} {IntegrityCommand.Verb} check [--base <git ref>]");
      output.WriteLine($"       {Name} {IntegrityCommand.Verb} seal <path>...");
      output.WriteLine($"       {Name} {LicensesCommand.Verb} scan --mode corpus|distribution [--root <dir>] [--report <file>] [--policy <file>]");
      output.WriteLine($"       {Name} {LicensesCommand.Verb} inventory [--policy <file>]");
      output.WriteLine($"       {Name} {BenchCommand.Verb} run (--id <id> | --all) [--out <dir>]");
      output.WriteLine($"       {Name} {BenchCommand.Verb} compare --baseline <dir> --current <dir> [--budgets <file>] [--out <file.md>]");
      output.WriteLine($"       {Name} {CatalogCommand.Verb} audit-adapter --audit <file> [--touch <path>]... [--corpus <dir>]");
      output.WriteLine($"       {Name} {ImportBitPermCommand.Verb} {ImportBitPermCommand.Import} [--fixtures <dir>] [--out <dir>] [--check]");
      output.WriteLine($"       {Name} {RebuildCommand.Verb} check --output <dir> [--runs <n>] [--input <path>]... [--wall-time <seconds>] [--memory <MiB>] -- <toolchain key> <args>");
      output.WriteLine($"       {Name} {RebuildCommand.Verb} compare <first dir> <second dir>");
      output.WriteLine($"       {Name} {GrammarCommand.Verb} generate <grammar> [--out <file.g.cs>] [--namespace <ns>] [--class <name>] [--profile <axis=value>]...");
      output.WriteLine($"       {Name} {GrammarCommand.Verb} read <grammar> [--tokens <file>]");
      output.WriteLine($"       {Name} {GrammarCommand.Verb} check [--root <dir>]");
      return Success;
    }

    if (args[0] == FloatImplementations) {
      if (args.Length == 2)
        return WriteImplementations(
          [.. FloatOperationSources.All.Select(source => new CSharpSource(source.Name, source.Text))],
          [.. HelperOperationSources.All.Select(source => new CSharpSource(source.Name, source.Text))],
          args[1], output, error);

      error.WriteLine($"{Name}: '{FloatImplementations}' takes one argument, the file to write.");
      return UsageError;
    }

    if (args[0] == ToolchainsCommand.Verb)
      return new ToolchainsCommand().Run(args[1..], output, error);

    if (args[0] == DiagnosticsCommand.Verb)
      return DiagnosticsCommand.Run(args[1..], output, error);

    if (args[0] == ErrorCatalogueCommand.Verb && args.Length > 1 && args[1] == ReportCapabilitiesCommand.Report)
      return ReportCapabilitiesCommand.Run(args[2..], output, error);

    if (args[0] == ErrorCatalogueCommand.Verb && args.Length > 1 && args[1] == ShowcaseCommand.Report)
      return ShowcaseCommand.Run(args[2..], output, error);

    if (args[0] == ErrorCatalogueCommand.Verb)
      return ErrorCatalogueCommand.Run(args[1..], output, error);

    if (args[0] == SpecCommand.Verb)
      return SpecCommand.Run(args[1..], output, error);

    if (args[0] == CorpusCommand.Verb)
      return new CorpusCommand().Run(args[1..], output, error);
    if (args[0] == CipherCommand.Verb)
      return new CipherCommand().Run(args[1..], output, error);
    if (args[0] == FuzzCommand.Verb)
      return new FuzzCommand().Run(args[1..], output, error);
    if (args[0] == IntegrityCommand.Verb)
      return new IntegrityCommand().Run(args[1..], output, error);
    if (args[0] == LicensesCommand.Verb)
      return new LicensesCommand().Run(args[1..], output, error);
    if (args[0] == BenchCommand.Verb)
      return new BenchCommand().Run(args[1..], output, error);
    if (args[0] == CatalogCommand.Verb)
      return new CatalogCommand().Run(args[1..], output, error);
    if (args[0] == ImportBitPermCommand.Verb)
      return ImportBitPermCommand.Run(args[1..], output, error);

    if (args[0] == CiCommand.Verb)
      return CiCommand.Run(args[1..], output, error);
    if (args[0] == RebuildCommand.Verb)
      return new RebuildCommand().Run(args[1..], output, error);
    if (args[0] == GrammarCommand.Verb)
      return new GrammarCommand().Run(args[1..], output, error);

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
  public static int WriteFloatImplementations(IReadOnlyList<CSharpSource> sources, string file, TextWriter output, TextWriter error)
    => Imported(sources, error) is { } module ? Write(module, file, output, error) : Failure;

  /// <summary>
  /// Imports both libraries of Polypiler's own code - the portable floating-point implementations and
  /// the helpers, each a class of its own - and writes them as one <see cref="IrModuleFormat"/> file
  /// (<see cref="ImplementationSources.Merge"/>); <see cref="FloatImplementations"/> does this.
  /// </summary>
  /// <param name="floats">The files of the floating-point implementations.</param>
  /// <param name="helpers">The files of the helpers.</param>
  /// <param name="file">The file to write.</param>
  /// <param name="output">Receives the name of the file written.</param>
  /// <param name="error">Receives the diagnostics of the imports and what keeps the file from being written.</param>
  /// <returns><see cref="Success"/>, or <see cref="Failure"/> when a library does not import, both declare a name or the file cannot be written.</returns>
  public static int WriteImplementations(IReadOnlyList<CSharpSource> floats, IReadOnlyList<CSharpSource> helpers, string file, TextWriter output, TextWriter error) {
    if (Imported(floats, error) is not { } first || Imported(helpers, error, HelperOperationSources.Intrinsics) is not { } second)
      return Failure;

    IrModule merged;
    try {
      merged = ImplementationSources.Merge(first, second);
    } catch (ArgumentException exception) {
      error.WriteLine($"{Name}: {exception.Message}");
      return Failure;
    }

    return Write(merged, file, output, error);
  }

  private static IrModule? Imported(IReadOnlyList<CSharpSource> sources, TextWriter error, IReadOnlyDictionary<string, IrIntrinsicOp>? intrinsics = null) {
    var imported = intrinsics is null ? CSharpFrontend.Import(sources) : CSharpFrontend.ImportLibrary(sources, intrinsics);
    foreach (var diagnostic in imported.Diagnostics)
      error.WriteLine(diagnostic);

    return imported.Module;
  }

  private static int Write(IrModule module, string file, TextWriter output, TextWriter error) {
    try {
      Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
      File.WriteAllBytes(file, IrModuleFormat.Write(module));
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      error.WriteLine($"{Name}: '{file}' cannot be written: {exception.Message}");
      return Failure;
    }

    output.WriteLine(file);
    return Success;
  }

}
