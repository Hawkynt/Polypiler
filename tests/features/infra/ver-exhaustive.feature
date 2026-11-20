@Layer.core
Feature: Exhaustive small-width equivalence prover

  The prover (#70) tries every input of an obligation: both sides are evaluated by the reference
  interpreter and compared by bit pattern. A width-generic obligation is taken at 8 bits. An input is
  excluded where a precondition is false or the before side is invalid; the after side must be valid
  and equal everywhere else.

  @Slow
  Scenario: A rotation written as two shifts is proved over every valid input
    Given the inputs "x: u<w>, n: u<w>"
    And the precondition "%p1 = UGe.u<w> %n, 1; %p2 = ULe.u<w> %n, 7; %p = And.bool %p1, %p2"
    And the before IR "%a = Shl.u<w>[strict] %x, %n; %s = Sub.u<w> 8, %n; %b = LShr.u<w>[strict] %x, %s; %r = Or.u<w> %a, %b"
    And the after IR "%r = RotateLeft.u<w> %x, %n"
    When the exhaustive prover runs with the default budget
    Then the result is Proved with proof class "exhaustive-8bit"
    And 1792 input combinations were evaluated

  Scenario: An arithmetic right shift is refuted with the first counterexample
    Given the inputs "x: i<w>"
    And the before IR "%a = Shl.i<w>[strict] %x, 1; %b = AShr.i<w>[strict] %x, 7; %r = Or.i<w> %a, %b"
    And the after IR "%r = RotateLeft.i<w> %x, 1"
    When the exhaustive prover runs with the default budget
    Then the result is Refuted with x = 0x80, before 0xFF and after 0x01
    And diagnostic PLY0056 is reported

  Scenario: The overflowing average is refuted where the carry is lost
    Given the inputs "a: u<w>, b: u<w>"
    And the before IR "%x = Xor.u<w> %a, %b; %h = LShr.u<w>[strict] %x, 1; %c = And.u<w> %a, %b; %r = Add.u<w> %c, %h"
    And the after IR "%s = Add.u<w> %a, %b; %r = LShr.u<w>[strict] %s, 1"
    When the exhaustive prover runs with the default budget
    Then the result is Refuted with a = 0x01, b = 0xFF, before 0x80 and after 0x00
    And diagnostic PLY0056 is reported

  Scenario: Two 16-bit inputs exceed the budget and nothing is claimed
    Given the inputs "a: u16, b: u16"
    And the before IR "%r = Add.u16 %a, %b"
    And the after IR "%r = Add.u16 %b, %a"
    When the exhaustive prover runs with the default budget
    Then the result is BudgetExceeded with 32 input bits
    And diagnostic PLY0057 is reported with mapped exit code 10

  Scenario: Preconditions that exclude every input prove nothing
    Given the inputs "x: u<w>"
    And the precondition "%p = ULt.u<w> %x, 0"
    And the before IR "%r = Add.u<w> %x, 1"
    And the after IR "%r = Sub.u<w> %x, 1"
    When the exhaustive prover runs with the default budget
    Then the result is Inconclusive with reason "vacuous: no input satisfies the preconditions"
    And warning PLY0055 is reported

  Scenario: A floating-point obligation is outside the prover's model
    Given the inputs "a: f32, b: f32"
    And the before IR "%r = Add.f32 %a, %b"
    And the after IR "%r = Add.f32 %b, %a"
    When the exhaustive prover runs with the default budget
    Then the result is Unsupported
    And diagnostic PLY0062 is reported with mapped exit code 3
