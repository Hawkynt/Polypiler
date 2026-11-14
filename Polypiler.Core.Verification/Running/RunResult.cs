using System;
using System.Collections.Generic;

namespace Polypiler.Core.Verification.Running;

/// <summary>How a run ended.</summary>
public enum RunOutcome {

  /// <summary>Every build step succeeded and the execute step ran to its end; its exit code may still be non-zero.</summary>
  Completed,

  /// <summary>A build step exited non-zero; nothing was executed.</summary>
  BuildFailed,

  /// <summary>A step ran longer than the wall-time limit and was killed.</summary>
  TimedOut,

  /// <summary>A step's process tree held more memory than the limit and was killed.</summary>
  MemoryExceeded,

  /// <summary>A step wrote more output than the limit and was killed.</summary>
  OutputExceeded,

  /// <summary>The plan was not started: its code is untrusted and the host cannot isolate it, or a toolchain is missing.</summary>
  Refused

}

/// <summary>How far a sandbox separates a run from the host.</summary>
public enum IsolationLevel {

  /// <summary>No network, a read-only file system outside the working directory, the caller's home hidden, and the limits.</summary>
  Isolated,

  /// <summary>The limits and the working directory only: enough for trusted code, never for untrusted code.</summary>
  Limited

}

/// <summary>
/// What a run produced: how it ended, the execute step's exit code and its output as bytes, never
/// decoded, and what the last step that ran used.
/// </summary>
/// <param name="Outcome">How the run ended.</param>
/// <param name="ExitCode">The execute step's exit code; -1 when a step was killed or nothing was executed.</param>
/// <param name="Stdout">The execute step's standard output, cut off at the output limit.</param>
/// <param name="Stderr">The execute step's standard error, cut off at the output limit.</param>
/// <param name="WallTime">How long the last step that ran took.</param>
/// <param name="CpuTime">The processor time the last step's process tree used, as far as it was sampled.</param>
/// <param name="PeakMemoryBytes">The largest resident memory of the last step's process tree that was sampled.</param>
/// <param name="Isolation">How far the sandbox separated the run from the host.</param>
/// <param name="Diagnostics">Why the run did not complete; empty when it did.</param>
public sealed record RunResult(
  RunOutcome Outcome,
  int ExitCode,
  ReadOnlyMemory<byte> Stdout,
  ReadOnlyMemory<byte> Stderr,
  TimeSpan WallTime,
  TimeSpan CpuTime,
  long PeakMemoryBytes,
  IsolationLevel Isolation,
  IReadOnlyList<Diagnostic> Diagnostics
) {

  /// <summary>The directory the run worked in, which no longer exists; <see langword="null"/> when the run was refused before one was made.</summary>
  public string? WorkingDirectory { get; init; }

}
