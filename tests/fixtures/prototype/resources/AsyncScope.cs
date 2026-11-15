// An await using statement, which is refused until asynchronous code is supported. Written for this
// repository.
namespace Polypiler.Fixtures.Prototype.Resources;

public static class AsyncScope {

  public static async System.Threading.Tasks.Task F(System.IAsyncDisposable r) {
    await using (r) {
    }
  }

}
