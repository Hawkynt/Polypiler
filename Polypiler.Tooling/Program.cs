using System;
using System.IO;

namespace Polypiler.Tooling;

/// <summary>Entry point of the <c>polypiler-tooling</c> command, the repository's maintenance tool.</summary>
public static class Program {

  /// <summary>The name the command is invoked by.</summary>
  public const string Name = "polypiler-tooling";

  /// <summary>Exit code for a successful run.</summary>
  public const int Success = 0;

  /// <summary>Exit code for a command line that could not be understood.</summary>
  public const int UsageError = 2;

  /// <summary>Runs the command line and returns the process exit code.</summary>
  public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

  /// <summary>Runs the command line against the given writers.</summary>
  public static int Run(string[] args, TextWriter output, TextWriter error) {
    if (args.Length == 0) {
      output.WriteLine($"usage: {Name} <verb> [options]");
      output.WriteLine("No verb is available yet.");
      return Success;
    }

    error.WriteLine($"{Name}: unknown verb '{args[0]}'.");
    return UsageError;
  }

}
