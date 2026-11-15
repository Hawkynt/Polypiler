// A resource of a struct type, which C# disposes as a copy without boxing; refused. Written for this
// repository.
using System;

namespace Polypiler.Fixtures.Prototype.Resources;

public struct Lease : IDisposable {

  public int Id;

  public void Dispose() {
    this.Id = 0;
  }

}

public static class StructScope {

  public static int F() {
    using (var lease = new Lease()) {
      return 1;
    }
  }

}
