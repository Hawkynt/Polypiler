@EndToEnd @Layer.rust
Feature: Rust frontend: functions, modules, closures and iterator closures
  Functions call each other and through pointers, closures that do not escape are inlined with
  their captures, and the closure-taking iterator methods run element by element as Rust's lazy
  iterators do. The expected values are rustc's (#464); RustPackageTests holds the module graph and
  the export set of the package modgraph to rustc, and its imported tests to cargo test.

  Scenario Outline: Imported functions and iterator chains compute what rustc computes
    Given the Rust source "<source>"
    When it is imported with overflowChecks panic
    And "f" is called with <args>
    Then the result is <expected>
    And rustc does the same

    Examples:
      | source                                                                                                                 | args          | expected |
      | pub fn f(n: u32) -> u64 { if n < 2 { n as u64 } else { f(n - 1) + f(n - 2) } }                                         | 10            | 55       |
      | pub fn f(x: u32) -> u32 { fn dbl(x: u32) -> u32 { x * 2 } let g: fn(u32) -> u32 = dbl; g(g(x)) }                       | 5             | 20       |
      | pub fn f(n: u32) -> u32 { (1..=n).map(\|x\| x * x).sum() }                                                             | 5             | 55       |
      | pub fn f(a: &[u32]) -> u32 { a.iter().filter(\|&&x\| x % 2 == 0).map(\|&x\| x * x).sum() }                            | [1 2 3 4 5 6] | 56       |
      | pub fn f(a: &[u32]) -> u32 { a.iter().fold(0u32, \|h, &x\| h.wrapping_mul(31).wrapping_add(x)) }                       | [1 2 3 4]     | 31810    |
      | pub fn f(n: u32) -> u32 { (1..=n).product() }                                                                          | 5             | 120      |

  Scenario: A closure that mutates a captured variable updates it
    Given the Rust source "pub fn f() -> u32 { let mut c = 0u32; let mut inc = |d: u32| { c += d; }; inc(2); inc(5); c }"
    When it is imported with overflowChecks panic
    And "f" is called with
    Then the result is 7
    And rustc does the same

  Scenario: Adapters run element by element and any stops early
    Given the Rust source "pub fn f(a: &[u32], z: u32) -> u32 { let mut n = 0u32; let found = a.iter().map(|&x| { n += 1; x / z }).any(|q| q > 1); n * 10 + found as u32 }"
    When it is imported with overflowChecks panic
    And "f" is called with [4 0 9], 2
    Then the result is 11
    And rustc does the same

  Scenario: Debug assertions need an explicit profile key
    Given the Rust source "pub fn f(x: u32) -> u32 { debug_assert!(x > 0); x - 1 }"
    When it is imported with overflowChecks wrap
    Then the import fails with diagnostic PLY1536
    And no IR is produced

  Scenario: Without debug assertions the assertion and its operands vanish
    Given the Rust source "pub fn f(x: u32) -> u32 { debug_assert!(x > 0); x - 1 }"
    When it is imported with overflowChecks wrap and debugAssertions off
    And "f" is called with 0
    Then the result is 4294967295

  Scenario: A function that returns a closure is rejected with a stable code
    Given the Rust source "fn adder(k: u32) -> impl Fn(u32) -> u32 { move |x| x + k }"
    When it is imported with overflowChecks panic
    Then the import fails with diagnostic PLY1530 at line 1
    And no IR is produced
