@EndToEnd @Layer.rust
Feature: Rust backend: enum globals and nullable objects converted to their base
  A global of an enum type is the enum itself behind a Mutex, as a floating-point global is; a
  reference to a derived class that may be null, converted to a base class that is a trait, is mapped
  to the trait's Rc, which an Option of the struct's Rc is not by itself.

  Scenario: A global of an enum type keeps its value from call to call
    Given the C# source "public enum Color : byte { Red = 1, Green = 2, Blue = 4 } public static class S { static Color current = Color.Green; public static int Swap(int c) { var old = current; current = (Color)c; return (int)old * 1000 + (int)current; } }"
    When it is translated to Rust
    Then the module contains "static CURRENT: Mutex<Color> = Mutex::new(Color::GREEN);"
    And the crate is clean under cargo fmt --check and cargo clippy -D warnings
    And with overflow checks panic and wrap the calls give
      | call | arguments | result |
      | Swap | 4         | 2004   |
      | Swap | 255       | 4255   |

  Scenario: A derived object that may be null is mapped to its base's trait
    Given the C# source "public class A { public virtual int M() => 1; } public sealed class B : A { public override int M() => 2; } public static class S { static int Of(A a) => a == null ? 0 : a.M(); public static int Upcast(int make) { B b = make != 0 ? new B() : null; A a = b; return Of(b) + (a == null ? 10 : 20); } }"
    When it is translated to Rust
    Then the module contains "let a: Option<Rc<dyn A>> = b.clone().map(|o| o as Rc<dyn A>);"
    And the crate is clean under cargo fmt --check and cargo clippy -D warnings
    And with overflow checks panic and wrap the calls give
      | call   | arguments | result |
      | Upcast | 1         | 22     |
      | Upcast | 0         | 10     |

  Scenario: A derived object that is never null is converted as it is, with no map
    Given the C# source "public class A { public virtual int M() => 1; } public sealed class B : A { public override int M() => 2; } public static class S { static int Of(A a) => a.M(); public static int F(int v) { B b = new B(); return Of(b) + v; } }"
    When it is translated to Rust
    Then the module contains no ".map(|o| o as Rc<dyn A>)"
    And with overflow checks panic and wrap the calls give
      | call | arguments | result |
      | F    | 40        | 42     |
