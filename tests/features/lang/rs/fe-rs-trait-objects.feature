@EndToEnd @Layer.rust
Feature: Rust frontend: trait objects
  A dyn Trait of the crate is an interface of the IR, and a value of a type that implements it
  becomes an object of a class of that type wherever the source expects &dyn Trait, Box<dyn Trait>
  or Rc<dyn Trait>; a call through it runs the impl of the value's type, chosen at run time. A
  &mut dyn Trait is refused: the object holds a copy of the value it borrows.

  Scenario: Boxed shapes of different types compute what rustc computes
    Given the Rust source "trait S { fn a(&self) -> u32; fn b(&self) -> u32 { self.a() + 1 } } struct Q(u32); impl S for Q { fn a(&self) -> u32 { self.0 * 2 } } impl S for u8 { fn a(&self) -> u32 { *self as u32 } } pub fn f(x: u32) -> u32 { let v: Vec<Box<dyn S>> = vec![Box::new(Q(x)), Box::new(7u8)]; let mut t = 0; for s in v.iter() { t += s.b(); } t }"
    When it is imported with overflowChecks panic
    And "f" is called with 20
    Then the result is 49
    And rustc does the same

  Scenario: A trait object that would write through to a borrowed value is rejected with a stable code
    Given the Rust source "trait T { fn m(&mut self); } pub fn f(x: &mut dyn T) { x.m() }"
    When it is imported with overflowChecks panic
    Then the import fails with diagnostic PLY1540 at line 1
    And the message names "writes through to a borrowed value"
