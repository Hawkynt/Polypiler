using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Polypiler.Core.Verification.Fuzzing;

/// <summary>Runs a target's inputs and judges each against a budget.</summary>
public interface IFuzzExecutor : IDisposable {

  /// <summary>How many times the executor started the code that runs the target; 0 before the first input.</summary>
  int Starts { get; }

  /// <summary>Runs one input and returns what it showed, judged against the budget.</summary>
  FuzzOutcome Execute(byte[] input, FuzzBudget budget);

}

/// <summary>
/// Runs a target on the calling thread. It sees crashes, mismatches and allocation, and a time
/// overrun only once the input has returned: an input that never returns hangs the caller. Inputs
/// that may not return belong in a <see cref="WorkerProcessExecutor"/>, which this executor serves
/// inside the worker.
/// </summary>
/// <param name="target">The target to run.</param>
public sealed class InProcessExecutor(IFuzzTarget target) : IFuzzExecutor {

  private int _starts;

  /// <inheritdoc />
  public int Starts => this._starts;

  /// <inheritdoc />
  public FuzzOutcome Execute(byte[] input, FuzzBudget budget) {
    ArgumentNullException.ThrowIfNull(budget);
    this._starts = 1;
    return Measure(target, input).Judge(budget);
  }

  /// <summary>
  /// Runs the target once and measures it, without a budget: the exception that escaped as a crash or
  /// mismatch, the bytes allocated on this thread and the time taken.
  /// </summary>
  public static FuzzOutcome Measure(IFuzzTarget target, ReadOnlySpan<byte> input) {
    ArgumentNullException.ThrowIfNull(target);
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var started = Stopwatch.GetTimestamp();
    try {
      Invoke(target, input);
    } catch (Exception exception) {
      return FuzzOutcome.Thrown(exception, target.GetType().Assembly, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore, Stopwatch.GetElapsedTime(started));
    }

    return new(null, "", GC.GetAllocatedBytesForCurrentThread() - allocatedBefore, Stopwatch.GetElapsedTime(started));
  }

  /// <summary>
  /// Calls the target from code the JIT leaves unoptimized: a hot interface call is otherwise
  /// devirtualized and the target's method inlined into its caller, which takes the target's frame out
  /// of the stack trace and changes the signature of the same crash halfway through a run.
  /// </summary>
  [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
  private static void Invoke(IFuzzTarget target, ReadOnlySpan<byte> input) => target.Execute(input);

  /// <summary>Nothing to release: the target runs on the caller's thread.</summary>
  public void Dispose() { }

}
