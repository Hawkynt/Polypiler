@EndToEnd @Layer.rust
Feature: Rust backend: single-owner classes as plain structs
  A class whose objects no two places hold at once - made, changed through one variable and dropped -
  is a plain struct with &self and &mut self methods, borrowed where it is passed and moved where it
  is returned. Every other class keeps the shared form, an Rc of cells.

  Scenario: A state object with a mutating method becomes a plain struct
    Given the C# source "public sealed class Xorshift32 { private uint _state; public Xorshift32(uint seed) { this._state = seed; } public uint Next() { var x = this._state; x ^= x << 13; x ^= x >> 17; x ^= x << 5; this._state = x; return x; } } public static class S { public static uint First(uint seed) => new Xorshift32(seed).Next(); public static uint Second(uint seed) { var g = new Xorshift32(seed); g.Next(); return g.Next(); } }"
    When it is translated to Rust
    Then the module contains "pub struct Xorshift32 {"
    And the module contains "state: u32,"
    And the module contains "pub fn new(seed: u32) -> Self {"
    And the module contains "pub fn next(&mut self) -> u32 {"
    And the module contains no "Rc<Xorshift32>"
    And the module contains no "Cell<"
    And the crate is clean under cargo fmt --check and cargo clippy -D warnings
    And with overflow checks panic and wrap the calls give
      | call   | arguments  | result     |
      | First  | 2463534242 | 723471715  |
      | Second | 2463534242 | 2497366906 |

  Scenario: An object held by two variables stays shared
    Given the C# source "public sealed class Counter { private int _n; public void Add(int v) { this._n += v; } public int Get() => this._n; } public static class S { public static int Aliased(int v) { var a = new Counter(); var b = a; b.Add(v); a.Add(1); return a.Get(); } }"
    When it is translated to Rust
    Then the module contains "Rc<Counter>"
    And with overflow checks panic and wrap the calls give
      | call    | arguments | result |
      | Aliased | 41        | 42     |

  Scenario Outline: What keeps a class shared
    Given the C# source "public sealed class C { private int _n; public void Set(int v) { this._n = v; } public int Get() => this._n; } <types>public static class S { <function> }"
    When it is translated to Rust
    Then the class C is <form>

    Examples:
      | types                                       | function                                                                                                               | form   |
      |                                             | public static int F(int v) { var c = new C(); c.Set(v); return c.Get(); }                                              | plain  |
      |                                             | public static int F(int v) { var c = new C(); Write(c, v); return c.Get(); } static void Write(C c, int v) => c.Set(v); | plain  |
      |                                             | public static bool F() { var a = new C(); var b = new C(); return ReferenceEquals(a, b); }                             | shared |
      |                                             | public static int F(bool b) { C c = null; if (b) c = new C(); return c == null ? 0 : c.Get(); }                        | shared |
      | public sealed class H { public C Kept; }    | public static int F() { var h = new H(); h.Kept = new C(); return h.Kept.Get(); }                                      | shared |
      |                                             | public static int F() => Getter()(); static System.Func<int> Getter() { var c = new C(); return () => c.Get(); }       | shared |
      |                                             | public static int F() => Same(new C()).Get(); static C Same(C c) => c;                                                 | shared |

  Scenario: The Rust fixtures with arrays as struct fields make the round trip
    Given the Rust fixtures types, edges and reassign
    When each is imported with overflow checks on and off and emitted back to Rust
    Then the crate builds with overflow checks on and off and every call gives what rustc recorded
