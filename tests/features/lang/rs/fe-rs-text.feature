@EndToEnd @Layer.rust
Feature: Rust frontend: String, &str and char
  Rust's text is UTF-8 and the IR's is UTF-16: the import keeps Rust's byte lengths, byte offsets and
  the panic of a byte range off a char boundary, Rust's order of strings, which is the order of code
  points, Rust's whitespace, and the integer parsing and formatting of core. The strings fixture holds
  every method to rustc in both overflow modes; these scenarios state the contract.

  Scenario Outline: Text computes what rustc computes
    Given the Rust source "<source>"
    When it is imported with overflowChecks panic
    And "f" is called with <args>
    Then the result is <expected>
    And rustc does the same

    Examples:
      | source                                                                                               | args   | expected |
      | pub fn f(n: u32) -> usize { let s = format!("é{}😀", n); s.len() * 100 + s.chars().count() }          | 42     | 804      |
      | pub fn f(n: u32) -> u32 { let s = n.to_string(); s.parse::<u32>().unwrap() + s.len() as u32 }        | 123    | 126      |
      | pub fn f(c: u32) -> bool { let a = char::from_u32(c).unwrap().to_string(); a.as_str() > "\u{ffff}" } | 128512 | true     |
      | pub fn f(n: u8) -> usize { "x".repeat(n as usize).trim().len() }                                     | 3      | 3        |

  Scenario: A byte range off a char boundary panics as Rust's slicing does
    Given the Rust source "pub fn f(n: usize) -> usize { let s = "aé"; s[..n].len() }"
    When it is imported with overflowChecks panic
    And "f" is called with 2
    Then the interpreter reports the panic panic
    And rustc does the same
