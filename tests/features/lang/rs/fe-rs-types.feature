@EndToEnd @Layer.rust
Feature: Rust frontend: structs, enums, Option, Result and the ? operator
  A struct is a record copied by value, a field-less enum an integer with the compiler's
  discriminants, Option an optional value and Result a tag with its payloads; `?` returns the `Err`
  or the `None` as a value, `unwrap` panics. The expected values are rustc's (#467).

  Scenario: A state struct with a mutating method is imported
    Given the Rust source "struct Xorshift32 { state: u32 } impl Xorshift32 { fn next(&mut self) -> u32 { let mut x = self.state; x ^= x << 13; x ^= x >> 17; x ^= x << 5; self.state = x; x } } pub fn two(seed: u32) -> (u32, u32) { let mut g = Xorshift32 { state: seed }; (g.next(), g.next()) }"
    When it is imported with overflowChecks wrap
    And "two" is called with 2463534242
    Then the result is (723471715, 2497366906)
    And rustc does the same
    And "Xorshift32_next" returns its result and its receiver

  Scenario Outline: Result values and the ? operator return errors as values
    Given the Rust source "#[derive(Clone, Copy, PartialEq)] enum E { DivZero, Neg } fn div(a: i32, b: i32) -> Result<i32, E> { if b == 0 { Err(E::DivZero) } else if a < 0 { Err(E::Neg) } else { Ok(a / b) } } fn calc(a: i32, b: i32, c: i32) -> Result<i32, E> { let q = div(a, b)?; div(q, c) } pub fn code(a: i32, b: i32, c: i32) -> i32 { match calc(a, b, c) { Ok(v) => v, Err(E::DivZero) => -1, Err(E::Neg) => -2 } }"
    When it is imported with overflowChecks panic
    And "code" is called with <a>, <b>, <c>
    Then the result is <expected>
    And rustc does the same

    Examples:
      | a   | b | c | expected |
      | 100 | 5 | 2 | 10       |
      | 1   | 0 | 2 | -1       |
      | -1  | 1 | 1 | -2       |
      | 10  | 2 | 0 | -1       |

  Scenario Outline: Enum discriminants and struct equality follow the compiler
    Given the Rust source "#[repr(u8)] enum Op { Add = 1, Sub = 5, Mul } #[derive(Clone, Copy, PartialEq)] struct P { x: i32, y: i32 } pub fn d() -> u8 { Op::Mul as u8 } pub fn eq(x: i32, y: i32) -> bool { P { x, y } == P { y: -4, x: 3 } }"
    When it is imported with overflowChecks panic
    And "<function>" is called with <args>
    Then the result is <expected>
    And rustc does the same

    Examples:
      | function | args  | expected |
      | d        |       | 6        |
      | eq       | 3, -4 | true     |
      | eq       | 3, 4  | false    |

  Scenario Outline: Option-returning accessors do not trap, unwrap does
    Given the Rust source "pub fn third(a: &[u8]) -> u8 { match a.get(2) { Some(&v) => v, None => 255 } } pub fn must(a: &[u8]) -> u8 { *a.get(2).unwrap() }"
    When it is imported with overflowChecks panic
    And "<function>" is called with <slice>
    Then rustc does the same

    Examples:
      | function | slice   |
      | third    | [1 2 3] |
      | third    | [1 2]   |
      | must     | [1 2]   |

  Scenario Outline: while let and let-else over Option are imported
    Given the Rust source "pub fn popsum(n: u32) -> u32 { let mut v = Vec::new(); for i in 1..=n { v.push(i); } let mut s = 0; while let Some(x) = v.pop() { s = s * 10 + x; } s } pub fn le(a: &[u32]) -> u32 { let Some(&v) = a.first() else { return 99 }; v + 1 }"
    When it is imported with overflowChecks panic
    And "<function>" is called with <args>
    Then the result is <expected>
    And rustc does the same

    Examples:
      | function | args | expected |
      | popsum   | 3    | 321      |
      | le       | [4]  | 5        |
      | le       | []   | 99       |

  Scenario: A trait object is rejected with a stable code and a pointer to the object model
    Given the Rust source "trait T { fn m(&self) -> u8; } pub fn f(x: &mut dyn T) -> u8 { x.m() }"
    When it is imported with overflowChecks panic
    Then the import fails with diagnostic PLY1540 at line 1
    And the message names "#473"
    And no IR is produced
