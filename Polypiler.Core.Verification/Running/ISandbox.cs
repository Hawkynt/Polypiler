using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Polypiler.Core.Verification.Running;

/// <summary>
/// The port through which the runner confines a process: it decides the command line that starts it,
/// which may wrap it in an isolating launcher, and watches the started process tree, measuring it and
/// enforcing the limits only the operating system can enforce.
/// </summary>
public interface ISandbox {

  /// <summary>How far the sandbox separates a run from the host.</summary>
  IsolationLevel Isolation { get; }

  /// <summary>The command line that runs a command inside the sandbox: the executable first, then its arguments.</summary>
  /// <param name="command">What to run and where.</param>
  IReadOnlyList<string> CommandLine(SandboxCommand command);

  /// <summary>Starts watching a process the runner has just started from <see cref="CommandLine"/>.</summary>
  /// <param name="process">The process.</param>
  /// <param name="limits">The limits of the step.</param>
  /// <returns>The monitor; the runner disposes of it after the step.</returns>
  IProcessTreeMonitor Watch(Process process, RunLimits limits);

}

/// <summary>What a sandbox runs: an executable with its arguments, in a working directory.</summary>
/// <param name="Executable">The full path of the executable.</param>
/// <param name="Arguments">The arguments, each passed as it is.</param>
/// <param name="WorkingDirectory">The run's directory, the only one the command may write to.</param>
public sealed record SandboxCommand(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory) {

  /// <summary>Folders the toolchain reads besides its own install root (<see cref="ToolchainResolution.Roots"/>), shown read-only where a sandbox hides them.</summary>
  public IReadOnlyList<string> ReadOnlyRoots { get; init; } = [];

}

/// <summary>Watches the process tree of one step.</summary>
public interface IProcessTreeMonitor : IDisposable {

  /// <summary>
  /// Measures the tree now and enforces the process limit where the operating system does not: a
  /// process beyond the limit is killed, as if it could not have been started.
  /// </summary>
  ResourceSample Sample();

  /// <summary>Kills every process of the tree that is still alive, the ones whose parent has already ended included.</summary>
  void KillAll();

}

/// <summary>One measurement of a process tree.</summary>
/// <param name="MemoryBytes">The resident memory of the whole tree.</param>
/// <param name="CpuTime">The processor time the tree has used so far.</param>
public readonly record struct ResourceSample(long MemoryBytes, TimeSpan CpuTime);
