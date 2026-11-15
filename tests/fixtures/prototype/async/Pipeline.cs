// Async methods of C# that await every task where they make it (#1330): a staged sum, awaits of
// completed tasks, a fan-out of independent squares with Task.WhenAll, the first fault in argument
// order, a fault before and after the first await observed in try, catch and finally, a ValueTask
// and tasks without a result. Written for this repository.
#nullable disable
using System;
using System.Threading.Tasks;

namespace Polypiler.Fixtures.Prototype.Async;

public static class Pipeline {

  public static async Task<int> Step(int x) {
    await Task.CompletedTask;
    return x * 3 + 1;
  }

  public static async Task<int> Run(int n) {
    var sum = 0;
    for (var i = 0; i < n; ++i)
      sum += await Step(i);

    return sum;
  }

  public static async Task<int> Completed() {
    await Task.CompletedTask.ConfigureAwait(false);
    var five = await Task.FromResult(5);
    return five + await Task.FromResult(2).ConfigureAwait(true);
  }

  private static async Task<int> Sq(int x) {
    await Task.CompletedTask;
    return x * x;
  }

  public static async Task<int[]> Squares() => await Task.WhenAll(Sq(2), Sq(3));

  public static async Task<int[]> NoSquares() => await Task.WhenAll<int>();

  public static async Task<int[]> OneSquare() => await Task.WhenAll(Sq(2));

  private static async Task<int> A() {
    await Task.CompletedTask;
    await Task.CompletedTask;
    throw new InvalidOperationException("A");
  }

  private static async Task<int> B() {
    await Task.CompletedTask;
    throw new InvalidOperationException("B");
  }

  public static async Task<string> Faults() {
    try {
      await Task.WhenAll(A(), B());
      return "none";
    } catch (InvalidOperationException e) {
      return e.Message;
    }
  }

  private static async Task<string> Thrower(bool before) {
    if (before)
      throw new InvalidOperationException("boom");

    await Task.CompletedTask;
    throw new InvalidOperationException("late");
  }

  public static async Task<string> Observed(bool before) {
    var trace = "t1";
    try {
      trace += " " + await Thrower(before);
    } catch (InvalidOperationException e) {
      trace += " caught " + e.Message;
    } finally {
      trace += " finally";
    }

    return trace;
  }

  public static async ValueTask<int> Valued(int x) {
    await Task.CompletedTask;
    return x + 1;
  }

  public static async Task<int> UsesValued() => await Valued(41);

  private static async Task Touch() {
    await Task.CompletedTask;
  }

  public static async Task<int> Both() {
    await Task.WhenAll(Touch(), Touch());
    await Touch();
    return 2;
  }

}
