@EndToEnd @Layer.rust
Feature: Rust frontend: branches, match, loops and evaluation order
  Rust loops, matches and labelled jumps take the path rustc takes: range bounds are evaluated once,
  an inclusive range ending at the type's maximum terminates, rev() and step_by() apply in source
  order, a step of zero panics before the first iteration, and the first arm whose pattern and
  guard hold wins.

  Scenario Outline: Imported control flow takes the path rustc takes
    Given the Rust function "<function>"
    When it is imported with overflowChecks panic and interpreted with <args>
    Then the result is <expected>
    And the same call compiled and run by rustc with the same overflow mode returns <expected>

    Examples:
      | function                                                                                                                              | args   | expected |
      | pub fn f(mut a: u32, mut b: u32) -> u32 { while b != 0 { let t = a % b; a = b; b = t; } a }                                            | 48, 18 | 6        |
      | pub fn f(mut n: u64) -> u32 { let mut s = 0; while n != 1 { n = if n % 2 == 0 { n / 2 } else { 3 * n + 1 }; s += 1; } s }              | 27     | 111      |
      | pub fn f(n: u32) -> u32 { let mut p = 1u32; loop { if p >= n { break p; } p <<= 1; } }                                                 | 100    | 128      |
      | pub fn f(n: u32) -> u32 { let mut s = 0; for i in 0..=n { s += i; } s }                                                                | 100    | 5050     |
      | pub fn f() -> u32 { let mut s = 0; for i in 0..10u32 { if i % 2 == 0 { continue; } s += i; } s }                                       |        | 25       |
      | pub fn f(x: i32) -> i32 { let y = if x < 0 { return -1 } else { x * 2 }; y + 1 }                                                       | -5     | -1       |
      | pub fn f(x: i32) -> i32 { let y = if x < 0 { return -1 } else { x * 2 }; y + 1 }                                                       | 4      | 9        |

  Scenario Outline: Match selects the first arm whose pattern and guard hold
    Given the Rust function "pub fn f(x: i32) -> u8 { match x { i32::MIN..=-1 => 0, 0 => 1, 1 | 2 => 2, n if n % 2 == 0 => 3, _ => 4 } }"
    When it is imported with overflowChecks panic and interpreted with <x>
    Then the result is <arm>
    And the same call compiled and run by rustc with the same overflow mode returns <arm>

    Examples:
      | x  | arm |
      | -5 | 0   |
      | 0  | 1   |
      | 2  | 2   |
      | 8  | 3   |
      | 7  | 4   |

  Scenario: A labelled break leaves both loops
    Given the Rust function "pub fn f(t: u32) -> u32 { let mut r = 0; 'outer: for i in 1..=9u32 { for j in 1..=9u32 { if i * j == t { r = i * 10 + j; break 'outer; } } } r }"
    When it is imported with overflowChecks panic and interpreted with 12
    Then the result is 26
    And interpreting with 11 returns 0

  Scenario: An inclusive range ending at the type maximum terminates
    Given the Rust function "pub fn f() -> u32 { let mut n = 0; for _ in 0..=u8::MAX { n += 1; } n }"
    When it is imported with overflowChecks panic and interpreted with
    Then the result is 256
    And the IR converts no value to u8 with a trapping contract

  Scenario: rev and step_by are applied in source order
    Given the Rust function "pub fn f(a: bool) -> u32 { let mut r = 0; if a { for i in (0..11u32).step_by(3).rev() { r = r * 16 + i; } } else { for i in (0..11u32).rev().step_by(3) { r = r * 16 + i; } } r }"
    When it is imported with overflowChecks panic and interpreted with true
    Then the result is 0x9630
    And interpreting with false returns 0xA741

  Scenario: A step of zero panics before the first iteration
    Given the Rust function "pub fn f(n: usize) -> u32 { let mut c = 0; for _ in (0..10u32).step_by(n) { c += 1; } c }"
    When it is imported with overflowChecks panic and interpreted with 0
    Then the interpreter reports the panic panic
    And the same call compiled and run by rustc with the same overflow mode panics with panic
    And interpreting with 3 returns 4

  Scenario: An unsupported conditional form is rejected with a stable code
    Given the Rust function "pub fn f(o: Option<u32>) -> u32 { if let Some(v) = o && v > 1 { v } else { 0 } }"
    When it is imported with overflowChecks panic
    Then the import fails with diagnostic PLY1510 at line 1
    And no IR function is produced
