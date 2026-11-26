@Layer.rust
Feature: Rust failures retain their diagnostics

  Scenario: Multiple modules do not hide a diagnostic
    Given PLY1592 "driver failed" and files "src/lib.rs", "src/a.rs" and "src/b.rs"
    When the Rust assertion failure message is formatted
    Then it contains PLY1592 and "driver failed"
    And it contains every file path and content
    And no exception is thrown

  Scenario: A diagnostic with no generated files is still reported
    Given PLY1592 "driver failed" and no generated files
    When the Rust assertion failure message is formatted
    Then it contains PLY1592 and "driver failed"
    And no exception is thrown
