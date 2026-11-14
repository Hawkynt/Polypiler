using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Polypiler.Core.Verification.Running;

/// <summary>How one step ended.</summary>
public enum StepEnd {

  /// <summary>The process ended by itself.</summary>
  Exited,

  /// <summary>The wall-time limit was reached.</summary>
  TimedOut,

  /// <summary>The memory limit was exceeded.</summary>
  MemoryExceeded,

  /// <summary>The output limit was exceeded.</summary>
  OutputExceeded

}

/// <summary>What one step produced.</summary>
/// <param name="End">How it ended.</param>
/// <param name="ExitCode">The exit code; -1 when the step was killed.</param>
/// <param name="Stdout">Standard output, cut off at the output limit.</param>
/// <param name="Stderr">Standard error, cut off at the output limit.</param>
/// <param name="WallTime">How long it took.</param>
/// <param name="CpuTime">The largest processor time of its tree that was sampled.</param>
/// <param name="PeakMemoryBytes">The largest resident memory of its tree that was sampled.</param>
public sealed record StepResult(StepEnd End, int ExitCode, byte[] Stdout, byte[] Stderr, TimeSpan WallTime, TimeSpan CpuTime, long PeakMemoryBytes);

/// <summary>
/// Runs one process inside a sandbox under the limits: the arguments as an array, a scrubbed
/// environment, both outputs captured as bytes up to the output limit, the tree sampled every 20 ms,
/// and the whole tree killed when a limit is broken, when the step is cancelled, and after the
/// process has ended, so that nothing it started survives it.
/// </summary>
public static class StepLauncher {

  /// <summary>How often the process tree is sampled.</summary>
  public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(20);

  /// <summary>
  /// The environment of every step, and nothing else: <c>PATH</c> (the toolchain's directory, then the
  /// system's default), <c>HOME</c> and the temporary directory set to the working directory,
  /// <c>LANG=C.UTF-8</c>, <c>TZ=UTC</c>, and the switches that keep the .NET SDK from phoning home
  /// and printing its banner. On Windows <c>SystemRoot</c>, without which a process cannot load the
  /// system libraries, <c>USERPROFILE</c>, Windows' <c>HOME</c>, and <c>APPDATA</c> and
  /// <c>LOCALAPPDATA</c> set to the working directory too, and <c>ProgramData</c>, <c>ProgramFiles</c> and
  /// <c>ProgramFiles(x86)</c>, which NuGet reads from the environment and cannot restore without.
  /// </summary>
  /// <param name="executable">The toolchain's executable.</param>
  /// <param name="workingDirectory">The run's directory.</param>
  public static IReadOnlyDictionary<string, string> Environment(string executable, string workingDirectory) {
    var toolchain = Path.GetDirectoryName(executable)!;
    var environment = new Dictionary<string, string>(StringComparer.Ordinal) {
      ["HOME"] = workingDirectory,
      ["LANG"] = "C.UTF-8",
      ["TZ"] = "UTC",
      ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
      ["DOTNET_NOLOGO"] = "1"
    };
    if (OperatingSystem.IsWindows()) {
      var system = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
      environment["PATH"] = string.Join(';', toolchain, Path.Combine(system, "System32"), system);
      environment["SystemRoot"] = system;
      environment["USERPROFILE"] = workingDirectory;
      environment["APPDATA"] = workingDirectory;
      environment["LOCALAPPDATA"] = workingDirectory;
      environment["ProgramData"] = System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData);
      environment["ProgramFiles"] = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
      environment["ProgramFiles(x86)"] = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);
      environment["TEMP"] = workingDirectory;
      environment["TMP"] = workingDirectory;
    } else {
      environment["PATH"] = string.Join(':', toolchain, "/usr/local/bin", "/usr/bin", "/bin");
      environment["TMPDIR"] = workingDirectory;
    }

    return environment;
  }

  /// <summary>Runs one step.</summary>
  /// <param name="sandbox">The sandbox to run it in.</param>
  /// <param name="executable">The toolchain's executable.</param>
  /// <param name="step">The step.</param>
  /// <param name="workingDirectory">The run's directory.</param>
  /// <param name="limits">The limits.</param>
  /// <param name="cancellationToken">Ends the step, its process tree killed.</param>
  /// <returns>What the step produced.</returns>
  /// <exception cref="OperationCanceledException">The token was cancelled; the tree has been killed.</exception>
  /// <exception cref="System.ComponentModel.Win32Exception">The process cannot be started.</exception>
  public static async Task<StepResult> RunAsync(ISandbox sandbox, string executable, ProcessSpec step, string workingDirectory, RunLimits limits, CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    var line = sandbox.CommandLine(new(executable, step.Arguments, workingDirectory));
    var info = new ProcessStartInfo(line[0]) {
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
      WorkingDirectory = workingDirectory
    };
    for (var index = 1; index < line.Count; ++index)
      info.ArgumentList.Add(line[index]);
    info.Environment.Clear();
    foreach (var (name, value) in Environment(executable, workingDirectory))
      info.Environment[name] = value;
    foreach (var (name, value) in step.Environment)
      info.Environment[name] = value;

    var capture = new OutputCapture(limits.OutputBytes);
    var clock = Stopwatch.StartNew();
    // The process is started, watched, killed and waited for on a thread of its own, and its pipes
    // are served by threads of their own: a busy thread pool can then delay neither a limit, nor a
    // cancellation, nor the capture. The thread that forked the process also outlives it, which
    // bubblewrap's --die-with-parent requires: Linux sends the parent's death signal when the forking
    // thread ends, not only when the parent process does.
    var (end, exitCode, peak, cpu) = await Dedicated(() => StartAndWatch(sandbox, info, step.Stdin, capture, limits, clock, cancellationToken)).ConfigureAwait(false);
    clock.Stop();
    if (end == StepEnd.Exited && capture.Exceeded)
      end = StepEnd.OutputExceeded;

    var (output, error) = capture.Close();
    return new(end, end == StepEnd.Exited ? exitCode : -1, output, error, clock.Elapsed, cpu, peak);
  }

  /// <summary>Runs blocking work on a thread of its own.</summary>
  private static Task<T> Dedicated<T>(Func<T> work)
    => Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

  /// <summary>Starts the process, puts it under the sandbox's watch, serves its pipes and watches it to its end.</summary>
  private static (StepEnd End, int ExitCode, long Peak, TimeSpan Cpu) StartAndWatch(ISandbox sandbox, ProcessStartInfo info, ReadOnlyMemory<byte> stdin, OutputCapture capture, RunLimits limits, Stopwatch clock, CancellationToken cancellationToken) {
    var process = Process.Start(info)!;
    IProcessTreeMonitor monitor;
    try {
      monitor = sandbox.Watch(process, limits);
    } catch {
      Kill(process, true);
      process.WaitForExit();
      process.Dispose();
      throw;
    }

    using (monitor) {
      Task[] pipes = [
        Dedicated(() => capture.Pump(process.StandardOutput.BaseStream, capture.Stdout)),
        Dedicated(() => capture.Pump(process.StandardError.BaseStream, capture.Stderr)),
        Dedicated(() => Feed(process.StandardInput.BaseStream, stdin))
      ];
      try {
        var (end, peak, cpu) = Watch(process, monitor, capture, limits, clock, cancellationToken);
        return (end, process.ExitCode, peak, cpu);
      } finally {
        // A pipe still open after the drain time is held by a reading thread; closing it under that
        // read could block, so the process and its pipes are then left to the finalizer.
        if (Drain(pipes))
          process.Dispose();
      }
    }
  }

  /// <summary>
  /// Samples the tree every 20 ms until the process ends, a limit is broken or the step is cancelled,
  /// then kills whatever of the tree is left: after a violation the whole tree, after a normal end the
  /// processes the program left behind.
  /// </summary>
  private static (StepEnd End, long Peak, TimeSpan Cpu) Watch(Process process, IProcessTreeMonitor monitor, OutputCapture capture, RunLimits limits, Stopwatch clock, CancellationToken cancellationToken) {
    var end = StepEnd.Exited;
    var peak = 0L;
    var cpu = TimeSpan.Zero;
    try {
      while (true) {
        var sample = monitor.Sample();
        peak = Math.Max(peak, sample.MemoryBytes);
        cpu = sample.CpuTime > cpu ? sample.CpuTime : cpu;
        if (sample.MemoryBytes > limits.MemoryBytes) {
          end = StepEnd.MemoryExceeded;
          break;
        }

        if (capture.Exceeded) {
          end = StepEnd.OutputExceeded;
          break;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var left = limits.WallTime - clock.Elapsed;
        if (left <= TimeSpan.Zero) {
          end = StepEnd.TimedOut;
          break;
        }

        if (process.WaitForExit(left < SampleInterval ? left : SampleInterval))
          break;
      }
    } finally {
      monitor.KillAll();
      Kill(process, false);
      process.WaitForExit();
    }

    return (end, peak, cpu);
  }

  /// <summary>How long the pipes may stay open after every process of the tree was killed.</summary>
  private static readonly TimeSpan _drainTime = TimeSpan.FromSeconds(10);

  /// <summary>Kills the process the runner started, its own child: one that has ended already is no error to <see cref="Process.Kill(bool)"/>.</summary>
  private static void Kill(Process process, bool entireTree) => process.Kill(entireTree);

  /// <summary>
  /// Waits for the pipes to close. A process that slipped out of the tree between two samples could
  /// keep them open for ever: after the drain time the runner stops waiting, and the capture, once
  /// closed, takes nothing more.
  /// </summary>
  /// <returns>Whether every pipe was closed in time.</returns>
  private static bool Drain(Task[] pipes) => Task.WaitAll(pipes, _drainTime);

  /// <summary>Writes standard input and closes it; a process that does not read it is no error.</summary>
  private static bool Feed(Stream input, ReadOnlyMemory<byte> bytes) {
    try {
      using (input)
        input.Write(bytes.Span);
    } catch (IOException) {
      // The process ended or closed its standard input before reading everything.
    }

    return true;
  }

  /// <summary>Both outputs of a step, counted together against the output limit.</summary>
  private sealed class OutputCapture(long limit) {

    private readonly Lock _gate = new();
    private long _total;

    public MemoryStream Stdout { get; } = new();

    public MemoryStream Stderr { get; } = new();

    /// <summary>Whether the two outputs together have carried more than the limit.</summary>
    public bool Exceeded {
      get {
        lock (this._gate)
          return this._total > limit;
      }
    }

    /// <summary>What both outputs carried so far; a pipe that is still open after the drain time adds nothing the caller sees.</summary>
    public (byte[] Stdout, byte[] Stderr) Close() {
      lock (this._gate)
        return (this.Stdout.ToArray(), this.Stderr.ToArray());
    }

    /// <summary>Copies one output into its buffer until the stream ends, keeping only what fits the limit.</summary>
    public bool Pump(Stream source, MemoryStream target) {
      var buffer = new byte[64 * 1024];
      int read;
      while ((read = source.Read(buffer)) > 0)
        this.Keep(buffer, read, target);

      return true;
    }

    private void Keep(byte[] buffer, int read, MemoryStream target) {
      lock (this._gate) {
        var keep = (int)Math.Clamp(limit - this._total, 0, read);
        target.Write(buffer, 0, keep);
        this._total += read;
      }
    }

  }

}
