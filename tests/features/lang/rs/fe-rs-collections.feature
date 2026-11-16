@EndToEnd @Layer.rust
Feature: Rust frontend: maps and sets
  HashMap, BTreeMap, HashSet and BTreeSet are the IR's maps and sets: insert hands back the value it
  replaces, indexing an absent key panics, entry(k).or_insert(v) inserts only where the key is
  absent. A HashMap enumerates in no order Rust specifies, so iterating one is refused.

  Scenario: Counting with entry computes what rustc computes
    Given the Rust source "use std::collections::HashMap; pub fn f(n: u32) -> u32 { let mut m = HashMap::new(); for i in 0..n { *m.entry(i % 3).or_insert(0u32) += i; } m[&1] * 10 + m.len() as u32 }"
    When it is imported with overflowChecks panic
    And "f" is called with 7
    Then the result is 53
    And rustc does the same

  Scenario: Iterating a HashMap is rejected with a stable code
    Given the Rust source "use std::collections::HashMap; pub fn f() -> u32 { let m: HashMap<u32, u32> = HashMap::new(); m.values().sum() }"
    When it is imported with overflowChecks panic
    Then the import fails with diagnostic PLY1525 at line 1
    And the message names "whose order is unspecified"

  Scenario: collect builds a map whose later key replaces the value of an earlier one
    Given the Rust source "use std::collections::HashMap; pub fn f(n: u32) -> u32 { let m: HashMap<u32, u32> = (0..n).map(|i| (i % 3, i)).collect(); m[&0] * 10 + m.len() as u32 }"
    When it is imported with overflowChecks panic
    And "f" is called with 7
    Then the result is 63
    And rustc does the same
