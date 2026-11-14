using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Polypiler.Tooling.Toolchains;

/// <summary>What a process printed and how it ended.</summary>
/// <param name="ExitCode">The exit code; -1 when the process timed out.</param>
/// <param name="StandardOutput">Everything written to standard output.</param>
/// <param name="StandardError">Everything written to standard error.</param>
/// <param name="TimedOut">Whether the process was killed because it outlived its time.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>Starts a process and waits for it; the seam the probe tests replace.</summary>
public interface IProcessRunner {

  /// <summary>Runs a command line to its end or until it times out.</summary>
  /// <param name="argv">The executable, then its arguments, passed as they are.</param>
  /// <param name="timeout">How long the process may run before it and its children are killed.</param>
  /// <exception cref="System.ComponentModel.Win32Exception">The executable cannot be started.</exception>
  ProcessResult Run(IReadOnlyList<string> argv, TimeSpan timeout);

}

/// <summary>Runs real processes, with nothing on standard input and both outputs captured.</summary>
public sealed class ProcessRunner : IProcessRunner {

  /// <inheritdoc />
  public ProcessResult Run(IReadOnlyList<string> argv, TimeSpan timeout) {
    var info = new ProcessStartInfo(argv[0]) {
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };
    for (var index = 1; index < argv.Count; ++index)
      info.ArgumentList.Add(argv[index]);

    using var process = Process.Start(info)!;
    process.StandardInput.Close();
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(timeout)) {
      process.Kill(true);
      process.WaitForExit();
      return new(-1, "", "", true);
    }

    process.WaitForExit();
    return new(process.ExitCode, output.Result, error.Result, false);
  }

}
