@EndToEnd @Layer.rust
Feature: Rust frontend: traits and generics
  A trait method is the method of the impl for the receiver's type, chosen when the crate is
  imported as rustc chooses it; a provided method is checked for every type that does not override
  it; a generic function is one function for each list of type arguments it is called with, as
  rustc monomorphises it. The operator traits make the operators call the impl's method.

  Scenario Outline: Traits and generics compute what rustc computes
    Given the Rust source "<source>"
    When it is imported with overflowChecks panic
    And "f" is called with <args>
    Then the result is <expected>
    And rustc does the same

    Examples:
      | source                                                                                                                                                                     | args | expected |
      | trait S { fn a(&self) -> u32; fn b(&self) -> u32 { self.a() + 1 } } struct Q(u32); impl S for Q { fn a(&self) -> u32 { self.0 * 2 } } pub fn f(x: u32) -> u32 { Q(x).b() } | 20   | 41       |
      | fn big<T: PartialOrd + Copy>(a: T, b: T) -> T { if a > b { a } else { b } } pub fn f(x: i64) -> i64 { big(x, 7) + big(3u8, 9) as i64 }                                      | -2   | 16       |
      | #[derive(Clone, Copy)] struct V(i32); impl std::ops::Add for V { type Output = V; fn add(self, o: V) -> V { V(self.0 + o.0) } } pub fn f(x: i32) -> i32 { (V(x) + V(x)).0 }  | 21   | 42       |

  Scenario: A generic function's arithmetic overflows in the type it is instantiated with
    Given the Rust source "fn twice<T: std::ops::Add<Output = T> + Copy>(x: T) -> T { x + x } pub fn f(x: u8) -> u8 { twice(x) }"
    When it is imported with overflowChecks panic
    And "f" is called with 200
    Then the interpreter reports the panic overflow
    And rustc does the same

  Scenario: A missing trait method is rejected as rustc rejects it
    Given the Rust source "trait T { fn a(&self); } struct S; impl T for S {}"
    When it is imported with overflowChecks panic
    Then the import fails with diagnostic PLY1500 at line 1
    And the message names "E0046"
