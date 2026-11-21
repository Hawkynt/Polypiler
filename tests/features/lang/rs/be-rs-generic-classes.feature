@EndToEnd @Layer.rust
Feature: Rust backend: generic classes as Rust generic structs
  A generic class of C# whose fields are values of its parameters, and whose members only hold,
  store, compare and default them, is one Rust generic struct, pub struct Ring<T>, with an
  impl<T: ...> Ring<T> bounded as a generic method is: Default and Copy always, Eq for Equals and Ord
  for the sign of CompareTo. An instantiation whose meaning a bound would change - a floating-point
  number, which .NET equates with its NaN - and a class the frontend could not state with its
  parameters keep one struct per instantiation, as before.

  Scenario: A ring buffer becomes one generic struct, used over long and over double
    Given the C# source "public sealed class Ring<T> { private readonly T[] _items; private int _count; public Ring(int capacity) { this._items = new T[capacity]; } public void Push(T value) { this._items[this._count % this._items.Length] = value; ++this._count; } public T Last => this._items[(this._count - 1) % this._items.Length]; } public static class Use { public static long LastLong(long a, long b) { var ring = new Ring<long>(2); ring.Push(a); ring.Push(b); ring.Push(9007199254740993); return ring.Last; } public static double LastDouble(double a) { var ring = new Ring<double>(1); ring.Push(a); return 1.0 / ring.Last; } }"
    When it is translated to Rust
    Then the module contains "pub struct Ring<T> {"
    And the module contains "impl<T: Default + Copy> Ring<T> {"
    And the module contains "Ring::<i64>::new(2)"
    And the module contains no "RingI64"
    And the crate is clean under cargo fmt --check and cargo clippy -D warnings
    And with overflow checks panic and wrap the crate gives what .NET gives for
      | call       | arguments                                 | result           |
      | LastLong   | -9223372036854775808, 9223372036854775807 | 9007199254740993 |
      | LastDouble | -0.0                                      | -Infinity        |
      | LastDouble | 4                                         | 0.25             |

  Scenario: Equals is Eq, and a box of double keeps its own struct with .NET's equality of NaN
    Given the C# source "public sealed class Box<T> where T : System.IEquatable<T> { private T _v; public Box(T v) { this._v = v; } public bool Is(T v) => this._v.Equals(v); } public static class Use { public static bool IsInt(int a, int b) { var box = new Box<int>(a); return box.Is(b); } public static bool IsDouble(double a, double b) { var box = new Box<double>(a); return box.Is(b); } }"
    When it is translated to Rust
    Then the module contains "impl<T: Eq + Default + Copy> Box<T> {"
    And the module contains "pub struct BoxF64 {"
    And the crate is clean under cargo fmt --check and cargo clippy -D warnings
    And with overflow checks panic and wrap the crate gives what .NET gives for
      | call     | arguments                | result |
      | IsInt    | -2147483648, -2147483648 | true   |
      | IsInt    | 0, 1                     | false  |
      | IsDouble | NaN, NaN                 | true   |
      | IsDouble | -0.0, 0.0                | true   |

  Scenario: A class whose template has no form in terms of its parameter keeps one struct per instantiation
    Given the C# source "public sealed class Cell<T> { private T _v; public Cell(T v) { this._v = v; } public string Show() => \"v\" + this._v; public T Get() => this._v; } public static class Use { public static int GetInt(int a) { var c = new Cell<int>(a); return c.Get(); } public static long GetLong(long a) { var c = new Cell<long>(a); return c.Get(); } }"
    When it is translated to Rust
    Then the module contains "pub struct CellI32 {"
    And the module contains no "<T"
    And the crate is clean under cargo fmt --check and cargo clippy -D warnings
    And with overflow checks panic and wrap the crate gives what .NET gives for
      | call    | arguments            | result               |
      | GetInt  | -2147483648          | -2147483648          |
      | GetLong | 9223372036854775807  | 9223372036854775807  |
