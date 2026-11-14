using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Polypiler.Core.Verification.Fuzzing;

/// <summary>
/// Runs a target in a worker process that speaks the <see cref="FuzzWorker"/> protocol, so that an
/// input which never returns or brings the process down is a finding and not the end of the run. An
/// input without a response within <see cref="FuzzBudget.PerInput"/> is a <see cref="FindingKind.Hang"/>:
/// the worker is killed. A worker that dies is a <see cref="FindingKind.Crash"/> with the signature
/// <c>process-exit:&lt;code&gt;</c>, and so is one that wrote to standard error and then failed to answer
/// in time: the runtime announces an unhandled exception or a fail-fast there before it ends the
/// process, and on Windows the error report can hold the dying process past the budget. Either way
/// the next input starts a new worker.
/// </summary>
/// <param name="startInfo">Creates the command line of a worker; called for every start.</param>
public sealed class WorkerProcessExecutor(Func<ProcessStartInfo> startInfo) : IFuzzExecutor {

  /// <summary>How long a worker may take to load its target and announce itself.</summary>
  public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

  private const int KeptErrorLength = 4096;

  private Process? _process;
  private readonly StringBuilder _error = new();
  private int _starts;
  private int _errorLines;

  /// <inheritdoc />
  public int Starts => this._starts;

  /// <inheritdoc />
  /// <exception cref="FuzzWorkerException">The worker cannot be started or does not announce itself.</exception>
  public FuzzOutcome Execute(byte[] input, FuzzBudget budget) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(budget);
    var process = this._process ?? this.Start();
    var started = Stopwatch.GetTimestamp();
    var errorLines = this.ErrorLines;
    Task<FuzzOutcome> response;
    try {
      FuzzWorker.WriteRequest(process.StandardInput.BaseStream, input);
      var stream = process.StandardOutput.BaseStream;
      response = OnOwnThread(() => FuzzWorker.ReadResponse(stream));
    } catch (IOException) {
      return this.Died(process, Stopwatch.GetElapsedTime(started));
    }

    bool finished;
    try {
      finished = response.Wait(budget.PerInput);
    } catch (AggregateException) {
      return this.Died(process, Stopwatch.GetElapsedTime(started));
    }

    if (!finished) {
      if (this.ErrorLines != errorLines)
        return this.Died(process, Stopwatch.GetElapsedTime(started));

      this.Stop(process);
      return new(FindingKind.Hang, FuzzOutcome.HangSignature, 0, Stopwatch.GetElapsedTime(started));
    }

    return response.Result.Judge(budget);
  }

  /// <summary>Ends the worker: asks it to stop, and kills it when it does not.</summary>
  public void Dispose() {
    if (this._process is not { } process)
      return;

    try {
      FuzzWorker.WriteEnd(process.StandardInput.BaseStream);
      process.StandardInput.Close();
    } catch (IOException) {
      // The worker is already gone; there is nothing left to ask.
    }

    if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
      this.Stop(process);
    else
      this.Forget(process);
  }

  private Process Start() {
    var info = startInfo();
    info.RedirectStandardInput = true;
    info.RedirectStandardOutput = true;
    info.RedirectStandardError = true;
    info.UseShellExecute = false;
    info.CreateNoWindow = true;
    Process process;
    try {
      process = Process.Start(info) ?? throw new FuzzWorkerException($"the worker '{info.FileName}' was not started");
    } catch (Win32Exception exception) {
      throw new FuzzWorkerException($"the worker '{info.FileName}' cannot be started: {exception.Message}");
    }

    lock (this._error) {
      this._error.Clear();
      this._errorLines = 0;
    }

    var errors = process.StandardError;
    OnOwnThread(() => {
      while (errors.ReadLine() is { } line)
        lock (this._error) {
          ++this._errorLines;
          if (this._error.Length < KeptErrorLength)
            this._error.AppendLine(line);
        }

      return 0;
    });
    ++this._starts;

    var announcement = process.StandardOutput.BaseStream;
    var ready = OnOwnThread(() => announcement.Read(new byte[1]));
    if (!ready.Wait(StartTimeout) || ready.Result != 1) {
      this.Stop(process);
      throw new FuzzWorkerException($"the worker '{info.FileName}' did not start: {this.ErrorText()}");
    }

    this._process = process;
    return process;
  }

  /// <summary>
  /// Runs a blocking read on a thread of its own. On Windows an asynchronous read of a pipe waits for a
  /// thread-pool thread, and a busy pool would turn a prompt answer into a hang.
  /// </summary>
  private static Task<T> OnOwnThread<T>(Func<T> read)
    => Task.Factory.StartNew(read, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

  private FuzzOutcome Died(Process process, TimeSpan elapsed) {
    // A worker whose output has ended is on its way out; one that lingers is helped along.
    if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
      process.Kill(true);
    process.WaitForExit();
    var code = process.ExitCode;
    this.Forget(process);
    return new(FindingKind.Crash, $"process-exit:{code}", 0, elapsed);
  }

  private void Stop(Process process) {
    try {
      process.Kill(true);
    } catch (InvalidOperationException) {
      // It ended on its own between the timeout and the kill.
    }

    process.WaitForExit();
    this.Forget(process);
  }

  private void Forget(Process process) {
    process.Dispose();
    if (ReferenceEquals(this._process, process))
      this._process = null;
  }

  private int ErrorLines {
    get {
      lock (this._error)
        return this._errorLines;
    }
  }

  private string ErrorText() {
    lock (this._error)
      return this._error.ToString().Trim();
  }

}

/// <summary>Thrown when a fuzz worker process cannot be started or does not announce itself.</summary>
public sealed class FuzzWorkerException : Exception {

  /// <summary>Creates the exception with the reason.</summary>
  public FuzzWorkerException(string message) : base(message) { }

}
