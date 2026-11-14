using System;
using System.IO;
using Polypiler.Core;

namespace Polypiler.Cli;

/// <summary>Entry point of the <c>polypiler</c> command.</summary>
public static class Program {

  /// <summary>Exit code for a successful run.</summary>
  public const int Success = (int)ExitCode.Success;

  /// <summary>Exit code for a command line that could not be understood.</summary>
  public const int UsageError = (int)ExitCode.UsageError;

  /// <summary>Runs the command line and returns the process exit code.</summary>
  public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

  /// <summary>Runs the command line against the given writers.</summary>
  public static int Run(string[] args, TextWriter output, TextWriter error) {
    if (args.Length == 0 || args[0] is "--help" or "-h" or "help") {
      output.WriteLine($"{ProductInfo.Name} {ProductInfo.Version}");
      output.WriteLine($"usage: {ProductInfo.Name} [--help | --version]");
      output.WriteLine("       " + TranslateCommand.UsageLine);
      output.WriteLine("translate is a prototype for small subsets of its languages; see the README for the planned interface.");
      return Success;
    }

    if (args[0] is "--version") {
      output.WriteLine(ProductInfo.Version);
      return Success;
    }

    if (args[0] is "translate")
      return TranslateCommand.Run(args[1..], output, error);

    error.WriteLine($"{ProductInfo.Name}: unknown command '{args[0]}'. Run '{ProductInfo.Name} --help'.");
    return UsageError;
  }

}
