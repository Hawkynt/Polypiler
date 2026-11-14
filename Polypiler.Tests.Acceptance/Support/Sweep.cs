using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Internal;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary>
/// Runs one check per target of a scenario that holds for every profile: the first target on the
/// scenario's thread, where it starts the executable, then the others side by side, in process. Every
/// target is checked, also after one failed; the scenario fails with the message of each target that
/// did.
/// </summary>
public static class Sweep {

  /// <summary>Checks every target; fails with one line per target that failed.</summary>
  /// <param name="targets">The targets, the one to start the executable first.</param>
  /// <param name="check">The check of one target; an assertion that fails it fails that target.</param>
  public static void Each<T>(IReadOnlyList<T> targets, Action<T> check) where T : notnull {
    var failures = new ConcurrentDictionary<int, string>();
    void Run(int index) {
      using var isolated = new TestExecutionContext.IsolatedContext();
      try {
        check(targets[index]);
      } catch (Exception exception) {
        failures[index] = $"{targets[index]}: {exception.Message.Trim()}";
      }
    }

    if (targets.Count == 0)
      Assert.Fail("a sweep needs at least one target");

    Run(0);
    Parallel.For(1, targets.Count, Run);
    Assert.That(failures.OrderBy(failure => failure.Key).Select(failure => failure.Value), Is.Empty,
      $"{failures.Count} of {targets.Count} targets failed");
  }

}
