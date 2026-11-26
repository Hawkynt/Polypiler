// Showcase: Tasks & Async/Await
// Category: Concurrency & Asynchrony
// Summary: Asynchronous Task<T> workflows using async/await, Task.FromResult, and Task.WhenAll.
using System.Threading.Tasks;

public static class TasksShowcase {
  private static async Task<int> ComputeAsync(int x)
    => await Task.FromResult(x * x + 1);

  public static async Task<int> CombineAsync(int a, int b) {
    int[] parts = await Task.WhenAll(ComputeAsync(a), ComputeAsync(b));
    return parts[0] + parts[1];
  }
}
