@Layer.rust
Feature: Rust CI layer

  @Slow
  Scenario: The smoke crate passes all four tools and prints the checksum
    Given the crate "tests/fixtures/infra/ci/rust/adler32"
    When I run "cargo fmt --check", "cargo clippy --all-targets -- -D warnings", "cargo test" and "cargo run --quiet" in the sandbox
    Then every command exits with 0
    And "cargo test" reports "1 passed"
    And "cargo run --quiet" prints "38600999"

  @Slow
  Scenario: Unformatted code fails cargo fmt
    Given the crate "tests/fixtures/infra/ci/rust/unformatted"
    When I run "cargo fmt --check" in the sandbox
    Then the exit code is 1
    And stdout contains a diff starting with "Diff in"

  @Slow
  Scenario: A clippy lint is an error
    Given the crate "tests/fixtures/infra/ci/rust/clippy-warn" whose function ends in "return x;"
    When I run "cargo clippy --all-targets -- -D warnings" in the sandbox
    Then the exit code is not 0
    And stderr contains "clippy::needless_return"

  @Slow
  Scenario: Miri detects an out-of-bounds unchecked read
    Given the crate "tests/fixtures/infra/ci/rust/miri-ub" whose test calls get_unchecked(4) on a slice of length 4
    When I run "cargo miri test" with the "rust-miri" toolchain in the sandbox
    Then the exit code is not 0
    And stderr contains "Undefined Behavior"

  Scenario: A crate without unsafe code skips Miri and says so
    Given the crate "tests/fixtures/infra/ci/rust/adler32"
    When the layer decides whether to run Miri
    Then Miri is not run
    And the test output contains "miri: not applicable (no unsafe code)"
