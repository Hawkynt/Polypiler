@EndToEnd @Layer.rust
Feature: Rust backend: generic methods as Rust generics
  A generic method of C# whose body only holds, stores, compares and defaults its values is one
  Rust generic function with the bounds those operations need: Copy, Default, Eq for Equals and Ord
  for the sign of CompareTo. An instantiation whose meaning a bound would change - a floating-point
  number, which .NET orders and equates with its NaN - and every operation without a bound keep one
  function per instantiation, as before.

  Scenario: Max over IComparable<T> becomes one generic function, called with u8 and i64 from the driver
    Given the C# methods "public static T Max<T>(T a, T b) where T : System.IComparable<T> => a.CompareTo(b) >= 0 ? a : b; public static byte MaxByte(byte a, byte b) => Max(a, b); public static long MaxLong(long a, long b) => Max(a, b);"
    When they are translated to Rust
    Then the module contains "pub fn max<T: Ord + Copy>(a: T, b: T) -> T {"
    And the module contains no "max_u8"
    And the crate is clean under cargo fmt --check and cargo clippy -D warnings
    And with overflow checks panic and wrap the crate gives what .NET gives for
      | call     | arguments                                 | result              |
      | Max<u8>  | 3, 200                                    | 200                 |
      | Max<u8>  | 255, 0                                    | 255                 |
      | Max<i64> | -9223372036854775808, 9223372036854775807 | 9223372036854775807 |
      | MaxByte  | 0, 0                                      | 0                   |

  Scenario: Max over double keeps its own function with .NET's order of NaN and the zeros
    Given the C# methods "public static T Max<T>(T a, T b) where T : System.IComparable<T> => a.CompareTo(b) >= 0 ? a : b; public static double MaxDouble(double a, double b) => Max(a, b); public static int MaxInt(int a, int b) => Max(a, b);"
    When they are translated to Rust
    Then the module contains "fn max_f64(a: f64, b: f64) -> f64"
    And the module contains "pub fn max<T: Ord + Copy>(a: T, b: T) -> T {"
    And the crate is clean under cargo fmt --check and cargo clippy -D warnings
    And with overflow checks panic and wrap the crate gives what .NET gives for
      | call      | arguments      | result    |
      | MaxDouble | NaN, -Infinity | -Infinity |
      | MaxDouble | -0.0, 0.0      | -0        |
      | MaxInt    | -1, 1          | 1         |

  Scenario: The value of CompareTo has no bound and stays one function per instantiation
    Given the C# methods "static int Cmp<T>(T a, T b) where T : System.IComparable<T> => a.CompareTo(b); public static int CmpByte(byte a, byte b) => Cmp(a, b); public static int CmpLong(long a, long b) => Cmp(a, b);"
    When they are translated to Rust
    Then the module contains "fn cmp_u8(a: u8, b: u8) -> i32"
    And the module contains no "<T"
    And the crate is clean under cargo fmt --check and cargo clippy -D warnings
    And with overflow checks panic and wrap the crate gives what .NET gives for
      | call    | arguments                                 | result |
      | CmpByte | 0, 255                                    | -255   |
      | CmpLong | -9223372036854775808, 9223372036854775807 | -1     |
