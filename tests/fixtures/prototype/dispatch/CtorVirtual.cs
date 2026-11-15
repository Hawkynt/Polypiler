// A virtual call from a base constructor: Init of Derived runs before the body of Derived's
// constructor. .NET has run the field initializer of Derived by then and Seen is 42; JavaScript
// would not have, so the translation is refused. Written for this repository.
namespace Polypiler.Fixtures.Prototype.Dispatch;

public class Starter {

  public int Seen;

  public Starter() {
    this.Init();
  }

  public virtual void Init() { }

}

public sealed class Derived : Starter {

  private readonly int _value = 42;

  public override void Init() {
    this.Seen = this._value;
  }

}

public static class CtorVirtualDemo {

  // 42 in .NET.
  public static int Seen() => new Derived().Seen;

}
