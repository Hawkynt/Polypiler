// Members reached through null: a method call, a field read, a field write and a property read
// each throw NullReferenceException in .NET and trap in every target. Written for this repository.
#nullable disable
namespace Polypiler.Fixtures.Prototype.Classes;

public sealed class Box {

  public int Value;

  public int Doubled => this.Value * 2;

  public int Get() => this.Value;

}

public static class NullDemo {

  public static int CallOnNull() {
    Box box = null;
    return box.Get();
  }

  public static int ReadOnNull() {
    Box box = null;
    return box.Value;
  }

  public static int WriteOnNull(int value) {
    Box box = null;
    box.Value = value;
    return value;
  }

  public static int PropertyOnNull() {
    Box box = null;
    return box.Doubled;
  }

  public static int NotNull(int value) {
    var box = new Box { Value = value };
    Box other = null;
    return box != null && other is null && box is not null ? box.Get() + box.Doubled : -1;
  }

}
