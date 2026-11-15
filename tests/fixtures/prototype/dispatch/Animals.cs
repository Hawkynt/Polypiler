// An animal and a dog: two virtual methods, one of which the override reaches through base, and a
// method of the base class that calls both. Written for this repository.
namespace Polypiler.Fixtures.Prototype.Dispatch;

public class Animal {

  public virtual int Legs() => 0;

  public virtual int Base() => 1;

  public int Describe() => this.Legs() * 10 + this.Base();

}

public sealed class Dog : Animal {

  public override int Legs() => 4;

  public override int Base() => base.Base() + 1;

}

public static class AnimalsDemo {

  // 42: 4 * 10 + (1 + 1).
  public static int DogDescribe() => new Dog().Describe();

  // 1: 0 * 10 + 1.
  public static int AnimalDescribe() => new Animal().Describe();

}
