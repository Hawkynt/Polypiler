@EndToEnd @Layer.rust
Feature: Rust frontend: numeric literals, arithmetic, overflow methods and casts
  Rust's `+` panics with overflow checks and wraps without them; the explicit methods ignore the
  mode, division panics in both, and `as` never panics. The import takes the mode as an option and
  never guesses it. The expected values are the ones rustc computes for the same source.

  Scenario Outline: Imported expressions compute what rustc computes in both overflow modes
    Given the Rust function "pub fn f(<params>) -> <ret> { <expr> }"
    When it is imported with overflowChecks <mode> and interpreted with <args>
    Then the result is <expected>
    And the same call compiled and run by rustc with the same overflow mode returns <expected>

    Examples:
      | params         | ret | expr                          | mode  | args            | expected    |
      | a: u8, b: u8   | u8  | a.wrapping_add(b)             | panic | 200, 100        | 44          |
      | a: u8, b: u8   | u8  | a + b                         | wrap  | 200, 100        | 44          |
      | a: u8, b: u8   | u8  | a.saturating_add(b)           | wrap  | 250, 10         | 255         |
      | a: u8, b: u8   | u8  | a.checked_add(b).unwrap_or(7) | panic | 200, 100        | 7           |
      | a: i32, b: i32 | i32 | a % b                         | panic | -7, 3           | -1          |
      | a: i32, b: i32 | i32 | a.rem_euclid(b)               | panic | -7, 3           | 2           |
      | a: u32, n: u32 | u32 | a << n                        | wrap  | 1, 33           | 2           |
      | a: i32         | i32 | a >> 1                        | panic | -8              | -4          |
      | a: i32         | u8  | a as u8                       | panic | 300             | 44          |
      | a: f64         | i32 | a as i32                      | panic | 3000000000.0    | 2147483647  |
      | a: f64         | i32 | a as i32                      | panic | NaN             | 0           |
      | a: u32         | u32 | a.rotate_left(8)              | panic | 0x12345678      | 0x34567812  |
      | a: i32, b: i32 | i32 | a.wrapping_div(b)             | panic | -2147483648, -1 | -2147483648 |
      | a: i32, b: i32 | i32 | a.div_euclid(b)               | wrap  | -7, 3           | -3          |
      | a: u32         | u32 | a.swap_bytes()                | wrap  | 0x12345678      | 0x78563412  |

  Scenario: Plain addition panics with overflow checks and wraps without
    Given the Rust function "pub fn f(a: i32, b: i32) -> i32 { a + b }"
    When it is imported with overflowChecks panic and interpreted with 2147483647, 1
    Then the interpreter reports the panic overflow
    And the IR contains the operator AddChecked
    And the same call compiled and run by rustc with the same overflow mode panics with overflow
    And imported with overflowChecks wrap the IR contains the operator Add and the result is -2147483648

  Scenario: Division traps do not depend on the overflow mode
    Given the Rust function "pub fn f(a: i32, b: i32, r: bool) -> i32 { if r { a % b } else { a / b } }"
    When it is imported with overflowChecks wrap and interpreted with 1, 0, false
    Then the interpreter reports the panic divide by zero
    And the IR contains the operator Divide
    And interpreting with -2147483648, -1, false reports the panic overflow
    And interpreting with -2147483648, -1, true reports the panic overflow

  Scenario: A shift by the full width panics with overflow checks
    Given the Rust function "pub fn f(a: u32, n: u32) -> u32 { a << n }"
    When it is imported with overflowChecks panic and interpreted with 1, 32
    Then the interpreter reports the panic overflow
    And the same call compiled and run by rustc with the same overflow mode panics with overflow
    And interpreting with 1, 31 returns 2147483648

  Scenario: Without an overflow mode and without a Cargo.toml, Cargo's dev profile panics
    Given the Rust function "pub fn f(a: i32, b: i32) -> i32 { a + b }"
    When it is imported without an overflow mode
    Then the overflow mode is panic, from "the default of Cargo profile 'dev', no Cargo.toml above the source"
    And interpreting with 2147483647, 1 reports the panic overflow

  Scenario: Without an overflow mode the Cargo profile beside the source decides
    Given the Rust function "pub fn f(a: i32, b: i32) -> i32 { a + b }"
    And a Cargo.toml beside it reading "[package]\nname = \"p\"\n[profile.dev]\noverflow-checks = false"
    When it is imported without an overflow mode
    Then the overflow mode is wrap, from "Cargo profile 'dev' of Cargo.toml"
    And interpreting with 2147483647, 1 returns -2147483648

  Scenario: A Cargo profile the manifest does not define is refused
    Given the Rust function "pub fn f(a: i32, b: i32) -> i32 { a + b }"
    And a Cargo.toml beside it reading "[package]\nname = \"p\""
    When it is imported without an overflow mode under the Cargo profile "fast"
    Then the import fails with diagnostic PLY1503 carrying "Cargo profile 'fast' is not defined in Cargo.toml"
    And no IR function is produced

  Scenario: An unsupported numeric type is rejected with a stable code
    Given the Rust function "pub fn f(a: u128) -> u128 { a + 1 }"
    When it is imported with overflowChecks wrap
    Then the import fails with diagnostic PLY1501 at line 1, column 13
    And no IR function is produced

  Scenario: Code the Rust compiler rejects is rejected
    Given the Rust function "pub fn f(a: u8, b: u32) -> u32 { a + b }"
    When it is imported with overflowChecks wrap
    Then the import fails with diagnostic PLY1500 carrying "E0308"
    And no IR function is produced
