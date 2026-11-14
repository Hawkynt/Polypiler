using System;
using System.Linq;
using System.Threading;

namespace Polypiler.Tests.Verification.Running.Support;

/// <summary>
/// A processor load for the tests that must not depend on an idle machine: threads of their own that
/// spin until the load is disposed. They are not threads of the pool, so the load competes for the
/// processors without taking a thread the code under test may need.
/// </summary>
internal sealed class CpuLoad : IDisposable {

  private readonly Thread[] _threads;
  private volatile bool _stopped;

  /// <summary>Starts the load.</summary>
  /// <param name="threads">How many threads spin.</param>
  public CpuLoad(int threads) {
    this._threads = [.. Enumerable.Range(0, threads).Select(index => new Thread(this.Spin) { IsBackground = true, Name = $"cpu-load-{index}" })];
    foreach (var thread in this._threads)
      thread.Start();
  }

  /// <summary>The load of the issue that asked for these tests: twice as many busy threads as the machine has processors.</summary>
  public static int TwiceTheProcessors => 2 * Environment.ProcessorCount;

  /// <summary>Stops every thread and waits for it.</summary>
  public void Dispose() {
    this._stopped = true;
    foreach (var thread in this._threads)
      thread.Join();
  }

  private void Spin() {
    var counter = 0UL;
    while (!this._stopped)
      counter = unchecked(counter * 6364136223846793005UL + 1442695040888963407UL);
    GC.KeepAlive(counter);
  }

}
