// Showcase: Exceptions & Resource Scopes
// Category: Error Handling
// Summary: Custom exceptions, catch filters (when), finally cleanup, and deterministic IDisposable using scopes.
using System;

public sealed class DomainException : Exception {
  public DomainException(string message) : base(message) { }
}

public sealed class ScopeTracker : IDisposable {
  public int Closed { get; private set; }
  public void Dispose() => this.Closed += 1;
}

public static class ExceptionsAndUsingShowcase {
  public static int Evaluate(int value) {
    var tracker = new ScopeTracker();
    int result = 0;
    try {
      using (tracker) {
        if (value < 0)
          throw new DomainException("negative");
        result = value * 2;
      }
    } catch (DomainException e) when (e.Message.Length > 0) {
      result = -1;
    } finally {
      result += tracker.Closed;
    }
    return result;
  }
}
