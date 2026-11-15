// Polymorphic recursion: Depth<T> calls Depth<T[]>, so the instantiations never end. .NET builds
// them lazily at run time and RecursionDemo.Run() returns 3; the IR has no instantiation set to
// build. Written for this repository.
namespace Polypiler.Fixtures.Prototype.Generics;

public static class RecursionDemo {

  static int Depth<T>(int n) => n == 0 ? 0 : 1 + Depth<T[]>(n - 1);

  public static int Run() => Depth<int>(3);

}
