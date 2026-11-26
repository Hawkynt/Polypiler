// Showcase: Multithreading & Synchronization (Boundary)
// Category: Unsupported Boundaries
// Summary: Atomic shared-memory synchronization and OS threads; explicitly refused until concurrency IR lands.
using System.Threading;

public static class ThreadsBoundary {
  private static int _shared;

  public static int IncrementAtomic(int delta)
    => Interlocked.Add(ref _shared, delta);
}
