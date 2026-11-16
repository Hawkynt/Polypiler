@EndToEnd @Layer.rust
Feature: Rust frontend: generic structs, impls and traits
  A generic struct is a struct of the IR for each list of type arguments the crate uses, and the
  methods of a generic impl and the generic methods are made once for each, as rustc monomorphises
  them. A generic trait is implemented once per list of its arguments. A public generic function is
  exported as the instances the crate makes of it.

  Scenario: A generic pair computes what rustc computes in the type it is instantiated with
    Given the Rust source "#[derive(Clone, Copy)] struct P<T> { a: T, b: T } impl<T: Copy + std::ops::Add<Output = T>> P<T> { fn sum(&self) -> T { self.a + self.b } } pub fn f(x: u8) -> u32 { P { a: x, b: 1 }.sum() as u32 + P { a: 1000u32, b: x as u32 }.sum() }"
    When it is imported with overflowChecks panic
    And "f" is called with 254
    Then the result is 1509
    And rustc does the same

  Scenario: A public generic function the crate instantiates nowhere is rejected with a stable code
    Given the Rust source "pub fn id<T>(x: T) -> T { x }"
    When it is imported with overflowChecks panic
    Then the import fails with diagnostic PLY1541 at line 1
    And the message names "instantiates nowhere"
