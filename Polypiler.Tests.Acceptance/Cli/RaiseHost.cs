using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Cli.Support;

namespace Polypiler.Tests.Acceptance.Cli;

/// <summary>
/// The entry point of this assembly, which makes it the test host of the exit-code tests (issue #23):
/// <c>dotnet Polypiler.Tests.Acceptance.dll &lt;arguments&gt;</c> runs <c>Program.Run</c> with the
/// commands of <see cref="RaiseCommand.Registry"/> on the process's own streams, so that the exit code
/// of a test-only command is observed by the operating system, as that of <c>polypiler</c> is. The test
/// runner loads the assembly as a library and never calls it.
/// </summary>
public static class RaiseHost {

  /// <summary>Runs a command line with the test commands, in the current directory.</summary>
  /// <param name="args">The arguments, without the program name.</param>
  /// <returns>The process exit code.</returns>
  public static int Main(string[] args)
    => global::Polypiler.Cli.Program.Run(args, Console.Out, Console.Error, RaiseCommand.Registry(), Directory.GetCurrentDirectory());

  /// <summary>Starts the test host as a child process and waits for it.</summary>
  /// <param name="workingDirectory">The directory it runs in.</param>
  /// <param name="environment">Variables set for it besides the inherited ones.</param>
  /// <param name="arguments">The arguments, without the program name.</param>
  /// <returns>Its exit code and its two streams.</returns>
  public static CommandResult Start(string workingDirectory, IReadOnlyDictionary<string, string> environment, params string[] arguments) {
    var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet") {
      WorkingDirectory = workingDirectory,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    start.ArgumentList.Add(typeof(RaiseHost).Assembly.Location);
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);

    foreach (var (name, value) in environment)
      start.Environment[name] = value;

    using var process = Process.Start(start)!;
    var error = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return new CommandResult(process.ExitCode, output, error.Result);
  }

}
